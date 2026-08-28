using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;
using CraneCAN.Core.Storage;
using CraneCAN.Driver.Onk160;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow : Window
{
    private const int MaximumVisibleFrames = 100_000;
    private readonly List<CanFrame> _rawFrames = [];
    private readonly FrameStatisticsAggregator _statisticsAggregator = new();
    private readonly List<ICanDriver> _drivers =
    [
        new Onk160SerialDriver(),
        new VirtualOnk160Driver()
    ];

    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private ICanDriver? _connectedDriver;
    private DateTimeOffset? _lastTimestamp;
    private ushort? _lastRodPressureRaw;
    private byte? _lastF7Status;
    private int _validOnkPackets;
    private int _invalidOnkPackets;
    private Onk160BenchCapture? _benchCapture;
    private Onk160BenchCaptureKind? _benchCaptureKind;
    private Onk160BenchSnapshot? _normalBenchSnapshot;
    private Onk160BenchSnapshot? _changedBenchSnapshot;
    private IReadOnlyList<Onk160BenchComparison> _allBenchComparison = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ProfileCombo.ItemsSource = BuiltInProfiles.All;
        DriverCombo.ItemsSource = _drivers;
        BenchCycleCountCombo.ItemsSource = new[] { 20, 30, 40, 50 };
        BenchCycleCountCombo.SelectedIndex = 0;
        DriverCombo.SelectedIndex = 0;
        ProfileCombo.SelectedIndex = 0;
        UpdateBenchControls();
        Loaded += async (_, _) => await RefreshChannelsAsync(showErrors: false);
    }

    public ObservableCollection<CanFrameRow> Frames { get; } = [];
    public ObservableCollection<FrameStatistics> Statistics { get; } = [];
    public ObservableCollection<Onk160PacketRow> OnkPackets { get; } = [];
    public ObservableCollection<Onk160BenchComparisonRow> BenchComparison { get; } = [];

    private CanSystemProfile? SelectedProfile => ProfileCombo.SelectedItem as CanSystemProfile;
    private ICanDriver? SelectedDriver => DriverCombo.SelectedItem as ICanDriver;
    private CanChannelDescriptor? SelectedChannel => ChannelCombo.SelectedItem as CanChannelDescriptor;

    private void ProfileCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not CanSystemProfile profile)
        {
            return;
        }

        DriverCombo.SelectedItem = _drivers.Single(driver => driver.Id == "onk160-serial");

        ApplyBitrateChoices();
        ProfileText.Text = FormatProfile(profile);
    }

    private async void DriverCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyBitrateChoices();
        ChannelCombo.IsEditable = true;
        if (IsLoaded)
        {
            await RefreshChannelsAsync(showErrors: false);
        }
    }

    private async void RefreshChannelsButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshChannelsAsync(showErrors: true);

    private void ApplyBitrateChoices()
    {
        BitrateCombo.ItemsSource = new[] { Onk160SerialDriver.RequiredBitrate };
        BitrateCombo.SelectedIndex = 0;
    }

    private async Task RefreshChannelsAsync(bool showErrors)
    {
        if (SelectedDriver is not { } driver)
        {
            return;
        }

        RefreshChannelsButton.IsEnabled = false;
        ChannelCombo.IsEnabled = false;
        try
        {
            var channels = await driver.DiscoverChannelsAsync();
            if (!ReferenceEquals(driver, SelectedDriver))
            {
                return;
            }

            ChannelCombo.ItemsSource = channels;
            ChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;
            if (channels.Count == 0)
            {
                StatusText.Text = "COM-порты не найдены автоматически. Введите COMx вручную или проверьте VCP-драйвер.";
            }
            else
            {
                StatusText.Text = $"Найдено каналов: {channels.Count}. Передача запрещена.";
            }
        }
        catch (Exception exception)
        {
            ChannelCombo.ItemsSource = null;
            StatusText.Text = "Не удалось обновить список каналов.";
            if (showErrors)
            {
                MessageBox.Show(FormatException(exception), "Ошибка поиска каналов",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            RefreshChannelsButton.IsEnabled = _connectedDriver is null;
            ChannelCombo.IsEnabled = _connectedDriver is null;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BitrateCombo.SelectedItem is not int bitrate)
            {
                MessageBox.Show("Выберите скорость обмена.", "CraneCAN Analyzer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var driver = SelectedDriver ?? throw new InvalidOperationException("Драйвер не выбран.");
            var channel = SelectedChannel;
            if (channel is null && driver.Id == "onk160-serial")
            {
                var manualPortName = ChannelCombo.Text.Trim();
                if (!string.IsNullOrWhiteSpace(manualPortName))
                {
                    channel = new CanChannelDescriptor(manualPortName, manualPortName);
                }
            }

            if (channel is null)
            {
                await RefreshChannelsAsync(showErrors: false);
                channel = SelectedChannel;

                if (channel is null && driver.Id == "onk160-serial")
                {
                    var manualPortName = ChannelCombo.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(manualPortName))
                    {
                        channel = new CanChannelDescriptor(manualPortName, manualPortName);
                    }
                }
            }

            if (channel is null)
            {
                throw new InvalidOperationException(
                    "COM-порт не найден. Введите номер вручную (например, COM4) или " +
                    "проверьте раздел «Порты (COM и LPT)» в Диспетчере устройств.");
            }

            await CloseOpenDriversAsync();
            await driver.OpenAsync(new CanChannelSettings(channel.Id, bitrate, ListenOnly: true));

            _connectedDriver = driver;
            _captureCancellation = new CancellationTokenSource();
            var connectionText = $"Подключено: {channel.Id}; ОНК-160; 38 400 бит/с; 8E1; только приём";
            SetConnectedState(true, connectionText);
            _captureTask = CaptureLoopAsync(driver, _captureCancellation.Token);
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка подключения",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await CloseOpenDriversAsync();
            _connectedDriver = null;
            SetConnectedState(false, "Ошибка подключения. Передача запрещена.");
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e) => await DisconnectAsync();

    private async Task CaptureLoopAsync(ICanDriver driver, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in driver.ReadFramesAsync(cancellationToken))
            {
                await Dispatcher.InvokeAsync(() => AddFrame(frame));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || !driver.IsOpen)
        {
        }
        catch (Exception exception)
        {
            Exception? closeException = null;
            try
            {
                await driver.CloseAsync();
            }
            catch (Exception cleanupError)
            {
                closeException = cleanupError;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var errorText = FormatException(exception);
                if (closeException is not null)
                {
                    errorText += Environment.NewLine + Environment.NewLine +
                                 "Дополнительная ошибка освобождения канала:" + Environment.NewLine +
                                 FormatException(closeException);
                }

                MessageBox.Show(errorText, "Ошибка приёма", MessageBoxButton.OK, MessageBoxImage.Error);
                _connectedDriver = null;
                SetConnectedState(false, "Приём остановлен из-за ошибки.");
            });
        }
    }

    private void AddFrame(CanFrame frame)
    {
        frame.Validate();
        var delta = _lastTimestamp.HasValue
            ? (frame.Timestamp - _lastTimestamp.Value).TotalMilliseconds
            : (double?)null;
        _lastTimestamp = frame.Timestamp;
        _rawFrames.Add(frame);
        _statisticsAggregator.Add(frame);

        Frames.Add(new CanFrameRow(frame, delta));
        if (Frames.Count > MaximumVisibleFrames)
        {
            Frames.RemoveAt(0);
        }

        OnkPackets.Add(new Onk160PacketRow(frame));
        if (OnkPackets.Count > MaximumVisibleFrames)
        {
            OnkPackets.RemoveAt(0);
        }
        UpdateOnkStatus(frame);
        ProcessBenchCapture(frame);

        if (_rawFrames.Count % 20 == 0)
        {
            RefreshStatistics();
        }

        FrameCountText.Text = $"Сообщений: {_rawFrames.Count:N0}";
    }

    private void UpdateOnkStatus(CanFrame frame)
    {
        if (frame.IsChecksumValid == true)
        {
            _validOnkPackets++;
        }
        else if (frame.IsChecksumValid == false)
        {
            _invalidOnkPackets++;
        }

        if (frame.IsChecksumValid != false && frame.Id == 0xE6)
        {
            _lastRodPressureRaw = Onk160Interpreter.ReadRodPressureRaw(frame.Data);
        }
        else if (frame.IsChecksumValid != false && frame.Id == 0xF7)
        {
            _lastF7Status = Onk160Interpreter.ReadF7Status(frame.Data);
        }

        var pressureText = !_lastRodPressureRaw.HasValue
            ? "E31 / датчик штоковой полости: нет данных"
            : _lastRodPressureRaw.Value == 0x8000
                ? "E31 / датчик штоковой полости: НЕИСПРАВНОСТЬ (0x8000)"
                : (_lastRodPressureRaw.Value & 0x8000) != 0
                    ? $"E31 / датчик штоковой полости: аварийное значение 0x{_lastRodPressureRaw.Value:X4}"
                    : $"E31 / датчик штоковой полости: норма, raw {_lastRodPressureRaw.Value} (0x{_lastRodPressureRaw.Value:X4})";

        string boomHeadText;
        string hookLimitText;
        if (!_lastF7Status.HasValue)
        {
            boomHeadText = "E55 / контроллер оголовка: нет данных";
            hookLimitText = "E83 / концевик подъёма крюка: нет данных";
        }
        else if (_lastF7Status.Value == 0x10)
        {
            boomHeadText = "E55 / контроллер оголовка: ПРЕДВАРИТЕЛЬНЫЙ ПРИЗНАК НЕИСПРАВНОСТИ (F7=10)";
            hookLimitText = "E83 / концевик подъёма крюка: не определяется без связи с КОС";
        }
        else
        {
            var boomHeadPresent = (_lastF7Status.Value & 0x01) != 0;
            var hookLimitNormal = (_lastF7Status.Value & 0x80) != 0;
            boomHeadText = boomHeadPresent
                ? $"E55 / контроллер оголовка: связь присутствует по эталону (F7={_lastF7Status.Value:X2})"
                : $"E55 / контроллер оголовка: состояние не определено (F7={_lastF7Status.Value:X2})";
            hookLimitText = !boomHeadPresent
                ? "E83 / концевик подъёма крюка: состояние не определено"
                : hookLimitNormal
                    ? "E83 / концевик подъёма крюка: норма"
                    : "E83 / концевик подъёма крюка: СРАБОТАЛ / ЦЕПЬ РАЗОМКНУТА";
        }

        OnkStatusText.Text = string.Join(Environment.NewLine,
            pressureText,
            boomHeadText,
            hookLimitText,
            $"Пакеты: контрольная сумма OK — {_validOnkPackets:N0}; ошибок — {_invalidOnkPackets:N0}");
    }

    private async void OpenCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Журнал ОНК-160 CSV (*.csv)|*.csv|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var frames = await CanCsvCodec.LoadAsync(dialog.FileName);
            ClearFrames();
            foreach (var frame in frames)
            {
                AddFrame(frame);
            }
            RefreshStatistics();
            StatusText.Text = $"Открыт журнал: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка чтения CSV",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Журнал ОНК-160 CSV (*.csv)|*.csv",
            FileName = $"onk160-trace-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await CanCsvCodec.SaveAsync(dialog.FileName, _rawFrames);
            StatusText.Text = $"Журнал сохранён: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка сохранения CSV",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearFrames();

    private void CaptureNormalButton_Click(object sender, RoutedEventArgs e) =>
        StartBenchCapture(Onk160BenchCaptureKind.Normal);

    private void CaptureChangedButton_Click(object sender, RoutedEventArgs e) =>
        StartBenchCapture(Onk160BenchCaptureKind.Changed);

    private void StartBenchCapture(Onk160BenchCaptureKind kind)
    {
        if (_connectedDriver is null)
        {
            MessageBox.Show("Сначала подключите реальный или виртуальный источник ОНК-160.",
                "Стендовые испытания", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(BenchTestNameTextBox.Text))
        {
            MessageBox.Show("Заполните поле «Название испытания». Это исключает потерю назначения записи.",
                "Стендовые испытания", MessageBoxButton.OK, MessageBoxImage.Warning);
            BenchTestNameTextBox.Focus();
            return;
        }

        if (kind == Onk160BenchCaptureKind.Changed && _normalBenchSnapshot is null)
        {
            MessageBox.Show("Сначала запишите устойчивое состояние нормы.",
                "Стендовые испытания", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (kind == Onk160BenchCaptureKind.Changed &&
            (string.IsNullOrWhiteSpace(BenchActionTextBox.Text) ||
             string.IsNullOrWhiteSpace(BenchBoiCodeTextBox.Text) ||
             string.IsNullOrWhiteSpace(BenchBoiTextTextBox.Text)))
        {
            MessageBox.Show(
                "Перед записью изменения заполните воздействие, код БОИ и текст БОИ. " +
                "Если кода или текста нет, явно напишите «нет».",
                "Стендовые испытания", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (kind == Onk160BenchCaptureKind.Normal &&
            (_normalBenchSnapshot is not null || _changedBenchSnapshot is not null) &&
            MessageBox.Show(
                "Новая запись нормы удалит текущее сравнение. Продолжить?",
                "Стендовые испытания",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var targetCycles = kind == Onk160BenchCaptureKind.Changed && _normalBenchSnapshot is not null
            ? _normalBenchSnapshot.CycleCount
            : BenchCycleCountCombo.SelectedItem is int selected ? selected : 20;
        if (kind == Onk160BenchCaptureKind.Normal)
        {
            _normalBenchSnapshot = null;
            _changedBenchSnapshot = null;
            _allBenchComparison = [];
            BenchComparison.Clear();
            BenchSummaryText.Text = "";
        }
        else
        {
            _changedBenchSnapshot = null;
            _allBenchComparison = [];
            BenchComparison.Clear();
        }

        _benchCapture = new Onk160BenchCapture(targetCycles);
        _benchCaptureKind = kind;
        BenchProgressBar.Minimum = 0;
        BenchProgressBar.Maximum = targetCycles;
        BenchProgressBar.Value = 0;
        BenchCaptureStatusText.Text = kind == Onk160BenchCaptureKind.Normal
            ? $"Запись нормы: ожидается {targetCycles} полных циклов…"
            : $"Запись изменённого состояния: ожидается {targetCycles} полных циклов…";
        UpdateBenchControls();
    }

    private void ProcessBenchCapture(CanFrame frame)
    {
        if (_benchCapture is null || !_benchCaptureKind.HasValue)
        {
            return;
        }

        var capture = _benchCapture;
        var kind = _benchCaptureKind.Value;
        var completed = capture.Append(frame);
        BenchProgressBar.Value = capture.CompletedCycles;
        BenchCaptureStatusText.Text = kind == Onk160BenchCaptureKind.Normal
            ? $"Запись нормы: {capture.CompletedCycles} из {capture.TargetCycles} циклов."
            : $"Запись изменения: {capture.CompletedCycles} из {capture.TargetCycles} циклов.";

        if (!completed)
        {
            return;
        }

        var snapshot = capture.CreateSnapshot();
        _benchCapture = null;
        _benchCaptureKind = null;
        if (kind == Onk160BenchCaptureKind.Normal)
        {
            _normalBenchSnapshot = snapshot;
            BenchCaptureStatusText.Text =
                $"Норма записана: {snapshot.CycleCount} циклов. Измените только один сигнал и нажмите «Записать изменение».";
        }
        else
        {
            _changedBenchSnapshot = snapshot;
            BenchCaptureStatusText.Text = $"Изменённое состояние записано: {snapshot.CycleCount} циклов. Сравнение готово.";
            RefreshBenchComparison();
        }

        UpdateBenchControls();
    }

    private void StopBenchCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_benchCapture is null)
        {
            return;
        }

        var completed = _benchCapture.CompletedCycles;
        _benchCapture = null;
        _benchCaptureKind = null;
        BenchCaptureStatusText.Text = $"Запись остановлена пользователем после {completed} полных циклов; неполная выборка не сохранена.";
        BenchProgressBar.Value = 0;
        UpdateBenchControls();
    }

    private void ResetBenchButton_Click(object sender, RoutedEventArgs e)
    {
        _benchCapture = null;
        _benchCaptureKind = null;
        _normalBenchSnapshot = null;
        _changedBenchSnapshot = null;
        _allBenchComparison = [];
        BenchComparison.Clear();
        BenchProgressBar.Value = 0;
        BenchCaptureStatusText.Text = _connectedDriver is null
            ? "Сначала подключитесь к ОНК-160 и запишите норму."
            : "Сравнение сброшено. Запишите норму.";
        BenchSummaryText.Text = "";
        UpdateBenchControls();
    }

    private void OnlyChangedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        RefreshBenchRows();
    }

    private void RefreshBenchComparison()
    {
        if (_normalBenchSnapshot is null || _changedBenchSnapshot is null)
        {
            _allBenchComparison = [];
            BenchComparison.Clear();
            return;
        }

        _allBenchComparison = Onk160BenchStateAnalyzer.Compare(_normalBenchSnapshot, _changedBenchSnapshot);
        RefreshBenchRows();
        var changedRows = _allBenchComparison.Count(row => row.IsChanged);
        var changedDataRows = _allBenchComparison.Count(row => row.IsChanged && !row.IsChecksum);
        var unstableRows = _allBenchComparison.Count(row =>
            !row.IsChecksum &&
            (row.NormalAgreementPercent < 90 || row.ChangedAgreementPercent < 90 ||
             row.NormalPresencePercent < 90 || row.ChangedPresencePercent < 90));
        BenchSummaryText.Text =
            $"Сравнено: {_normalBenchSnapshot.CycleCount} + {_changedBenchSnapshot.CycleCount} циклов. " +
            $"Изменено строк: {changedRows}; информационных байтов/признаков: {changedDataRows}. " +
            "Изменение контрольной суммы показано отдельно и не считается самостоятельным сигналом." +
            (unstableRows > 0
                ? $" ВНИМАНИЕ: нестабильных строк (<90%): {unstableRows}; испытание желательно повторить."
                : " Оба состояния устойчивы: нестабильных строк не обнаружено.");
    }

    private void RefreshBenchRows()
    {
        BenchComparison.Clear();
        var onlyChanged = OnlyChangedCheckBox?.IsChecked == true;
        foreach (var row in _allBenchComparison.Where(row => !onlyChanged || row.IsChanged))
        {
            BenchComparison.Add(new Onk160BenchComparisonRow(row));
        }
    }

    private async void SaveBenchReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_normalBenchSnapshot is null || _changedBenchSnapshot is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Отчёт стендового испытания CSV (*.csv)|*.csv",
            FileName = $"onk160-bench-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var metadata = new Onk160BenchReportMetadata(
                DateTimeOffset.UtcNow,
                BenchTestNameTextBox.Text.Trim(),
                BenchActionTextBox.Text.Trim(),
                BenchBoiCodeTextBox.Text.Trim(),
                BenchBoiTextTextBox.Text.Trim(),
                _normalBenchSnapshot.CycleCount,
                _changedBenchSnapshot.CycleCount);
            await Onk160BenchReportCodec.SaveAsync(dialog.FileName, metadata, _allBenchComparison);
            StatusText.Text = $"Отчёт стендового испытания сохранён: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка сохранения отчёта",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateBenchControls()
    {
        var isCapturing = _benchCapture is not null;
        var isConnected = _connectedDriver is not null;
        CaptureNormalButton.IsEnabled = isConnected && !isCapturing;
        CaptureChangedButton.IsEnabled = isConnected && !isCapturing && _normalBenchSnapshot is not null;
        StopBenchCaptureButton.IsEnabled = isCapturing;
        SaveBenchReportButton.IsEnabled = !isCapturing &&
                                          _normalBenchSnapshot is not null &&
                                          _changedBenchSnapshot is not null;
        BenchCycleCountCombo.IsEnabled = !isCapturing && _normalBenchSnapshot is null;
        BenchTestNameTextBox.IsEnabled = !isCapturing && _normalBenchSnapshot is null;
        BenchActionTextBox.IsEnabled = !isCapturing && _changedBenchSnapshot is null;
        BenchBoiCodeTextBox.IsEnabled = !isCapturing && _changedBenchSnapshot is null;
        BenchBoiTextTextBox.IsEnabled = !isCapturing && _changedBenchSnapshot is null;
        SaveTraceButton.IsEnabled = !isCapturing;
        ClearTrafficButton.IsEnabled = !isCapturing;
        OpenCsvButton.IsEnabled = !isCapturing && !isConnected;
    }

    private void ClearFrames()
    {
        Frames.Clear();
        Statistics.Clear();
        OnkPackets.Clear();
        _rawFrames.Clear();
        _statisticsAggregator.Clear();
        _lastTimestamp = null;
        _lastRodPressureRaw = null;
        _lastF7Status = null;
        _validOnkPackets = 0;
        _invalidOnkPackets = 0;
        OnkStatusText.Text = "Нет данных ОНК-160.";
        FrameCountText.Text = "Сообщений: 0";
    }

    private void RefreshStatistics()
    {
        Statistics.Clear();
        foreach (var item in _statisticsAggregator.Snapshot())
        {
            Statistics.Add(item);
        }
    }

    private async Task DisconnectAsync()
    {
        var cancellation = _captureCancellation;
        _captureCancellation = null;
        cancellation?.Cancel();

        await CloseOpenDriversAsync();
        if (_captureTask is not null)
        {
            try
            {
                await _captureTask;
            }
            catch (OperationCanceledException)
            {
            }
            _captureTask = null;
        }

        cancellation?.Dispose();
        _connectedDriver = null;
        if (_benchCapture is not null)
        {
            var completed = _benchCapture.CompletedCycles;
            _benchCapture = null;
            _benchCaptureKind = null;
            BenchCaptureStatusText.Text = $"Запись прервана при отключении после {completed} полных циклов.";
            BenchProgressBar.Value = 0;
        }
        RefreshStatistics();
        SetConnectedState(false, "Отключено. Передача запрещена.");
    }

    private async Task CloseOpenDriversAsync()
    {
        foreach (var driver in _drivers.Where(driver => driver.IsOpen))
        {
            await driver.CloseAsync();
        }
    }

    private void SetConnectedState(bool connected, string status)
    {
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        ProfileCombo.IsEnabled = !connected;
        DriverCombo.IsEnabled = !connected;
        ChannelCombo.IsEnabled = !connected;
        RefreshChannelsButton.IsEnabled = !connected;
        BitrateCombo.IsEnabled = !connected;
        OpenCsvButton.IsEnabled = !connected;
        StatusText.Text = status;
        UpdateBenchControls();
    }

    private static string FormatProfile(CanSystemProfile profile)
    {
        var lines = new List<string>
        {
            profile.DisplayName,
            new('=', profile.DisplayName.Length),
            $"Протокол: {profile.Protocol}",
            $"Подтверждённая скорость: {(profile.ConfirmedBitrate.HasValue ? FormatBitrate(profile.ConfirmedBitrate.Value) : "неизвестна")}",
            $"Проверяемые скорости: {string.Join(", ", profile.CandidateBitrates.Select(FormatBitrate))}",
            "",
            "Узлы / логические адреса:"
        };
        lines.AddRange(profile.Nodes.Select(node =>
            $"  {node.Address} (0x{node.Address:X2})  {node.Name}  [{node.Confidence}]"));
        lines.Add("");
        lines.Add("Диагностика:");
        lines.AddRange(profile.Diagnostics.Select(item => $"  {item.Code,-9} {item.Meaning}"));
        lines.Add("");
        lines.Add("Дискретные сигналы:");
        lines.AddRange(profile.DiscreteSignals.Select(item => $"  {item.Channel}  {item.Meaning}"));
        lines.Add("");
        lines.Add($"Примечание: {profile.Notes}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBitrate(int value) => value % 1000 == 0
        ? $"{value / 1000} кбит/с"
        : $"{value:N0} бит/с";

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(Environment.NewLine,
            messages.Select((message, index) => $"{index + 1}. {message}"));
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        await DisconnectAsync();
        foreach (var driver in _drivers)
        {
            await driver.DisposeAsync();
        }
    }
}

public sealed record CanFrameRow
{
    public CanFrameRow(CanFrame frame, double? deltaMilliseconds)
    {
        TimeText = frame.Timestamp.ToString("HH:mm:ss.fffffff");
        DeltaText = deltaMilliseconds?.ToString("F3") ?? "-";
        Channel = frame.Channel;
        Direction = frame.Direction.ToString();
        ProtocolText = "ОНК UART";
        IdText = frame.IdText;
        Dlc = frame.Dlc;
        DataText = frame.DataText;
        TypeText = frame.IsChecksumValid switch
        {
            true => "UART / сумма OK",
            false => "UART / ошибка",
            null => "UART / raw"
        };
        DecodedText = Onk160Interpreter.Describe((byte)frame.Id, frame.Data, frame.IsChecksumValid);
    }

    public string TimeText { get; }
    public string DeltaText { get; }
    public int Channel { get; }
    public string Direction { get; }
    public string ProtocolText { get; }
    public string IdText { get; }
    public int Dlc { get; }
    public string DataText { get; }
    public string TypeText { get; }
    public string DecodedText { get; }
}

public sealed record Onk160PacketRow
{
    public Onk160PacketRow(CanFrame frame)
    {
        TimeText = frame.Timestamp.ToString("HH:mm:ss.fffffff");
        HeaderText = frame.Id.ToString("X2");
        Length = frame.Dlc;
        DataText = frame.DataText;
        ChecksumText = frame.IsChecksumValid switch
        {
            true => "OK",
            false => "ОШИБКА",
            null => "не определена"
        };
        DecodedText = Onk160Interpreter.Describe((byte)frame.Id, frame.Data, frame.IsChecksumValid);
    }

    public string TimeText { get; }
    public string HeaderText { get; }
    public int Length { get; }
    public string DataText { get; }
    public string ChecksumText { get; }
    public string DecodedText { get; }
}

public sealed record Onk160BenchComparisonRow
{
    public Onk160BenchComparisonRow(Onk160BenchComparison comparison)
    {
        HeaderText = comparison.Header.ToString("X2");
        Field = comparison.Field;
        NormalHex = FormatByte(comparison.NormalValue);
        ChangedHex = FormatByte(comparison.ChangedValue);
        XorHex = FormatByte(comparison.XorMask);
        ChangedBits = comparison.ChangedBits;
        IsChanged = comparison.IsChanged;
        NormalStabilityText = comparison.NormalValue.HasValue
            ? $"{comparison.NormalAgreementPercent:F1}%"
            : "—";
        ChangedStabilityText = comparison.ChangedValue.HasValue
            ? $"{comparison.ChangedAgreementPercent:F1}%"
            : "—";
        PresenceText = $"{comparison.NormalPresencePercent:F1}% → {comparison.ChangedPresencePercent:F1}%";
    }

    public string HeaderText { get; }
    public string Field { get; }
    public string NormalHex { get; }
    public string ChangedHex { get; }
    public string XorHex { get; }
    public string ChangedBits { get; }
    public bool IsChanged { get; }
    public string NormalStabilityText { get; }
    public string ChangedStabilityText { get; }
    public string PresenceText { get; }

    private static string FormatByte(byte? value) => value.HasValue ? value.Value.ToString("X2") : "—";
}
