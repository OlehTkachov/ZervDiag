using System.Windows;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Models;
using CraneCAN.Driver.PcanBasic;

namespace CraneCAN.App;

public partial class MainWindow
{
    private bool _pcanDiagnosticBusy;

    private async void DiagnosePcanLiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pcanDiagnosticBusy)
            return;

        if (_liveConnectionReady)
        {
            MessageBox.Show(
                "Live CAN уже подключён. Сначала нажмите «ОТКЛЮЧИТЬ» во вкладке LIVE GUIDED, затем повторите аппаратную проверку.",
                "Проверка PCAN / Live CAN",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _pcanDiagnosticBusy = true;
        var sourceElement = sender as FrameworkElement;
        if (sourceElement is not null)
            sourceElement.IsEnabled = false;

        try
        {
            StatusText.Text = "PCAN: поиск USB-каналов…";
            await using var discovery = new PcanBasicCanDriver();
            var channels = await discovery.DiscoverChannelsAsync();
            if (channels.Count == 0)
            {
                ShowPcanDiagnosticResult(
                    "PCAN-USB не обнаружен среди доступных каналов. Каналы, занятые другим PCAN-Basic клиентом, намеренно исключаются. " +
                    "Проверьте USB, PEAK Device Driver x64 и наличие PCANBasic.dll." +
                    Environment.NewLine + Environment.NewLine +
                    FormatPcanTrace(discovery.GetOperationDiagnostics()),
                    MessageBoxImage.Warning);
                return;
            }

            var channel = channels[0];
            LiveSourceCombo.SelectedIndex = 1;
            LiveChannelCombo.ItemsSource = channels;
            LiveChannelCombo.SelectedItem = channel;
            if (LiveChannelCombo.SelectedItem is null)
                LiveChannelCombo.SelectedIndex = 0;

            LiveBitrateCombo.ItemsSource = PcanLiveProbe.CommonBitrates;
            LiveBitrateCombo.SelectedItem = 250_000;

            StatusText.Text = $"PCAN найден: {channel.DisplayName}. Пассивно ищу bitrate…";
            LiveInstructionText.Text =
                "Проверяю реальный CAN только в LISTEN ONLY. Передача CAN отсутствует. На каждой скорости слушаю до 2 секунд.";

            var report = await PcanLiveProbe.ProbeAsync(
                channel.Id,
                PcanLiveProbe.CommonBitrates,
                TimeSpan.FromSeconds(2));

            if (report.DetectedBitrate is not int bitrate || report.BestAttempt is not { } best)
            {
                LiveConnectionText.Text = "NO CAN FRAMES";
                LiveInstructionText.Text =
                    "PCAN-USB найден, но реальные CAN-кадры не получены. Проверьте CAN_H/CAN_L/GND, питание сети и фактический bitrate.";
                ShowPcanDiagnosticResult(
                    $"Адаптер найден: {channel.DisplayName}{Environment.NewLine}{Environment.NewLine}" +
                    $"{report.Describe()}{Environment.NewLine}{Environment.NewLine}" +
                    $"Полный журнал попыток:{Environment.NewLine}{report.DescribeDetailed()}{Environment.NewLine}{Environment.NewLine}" +
                    "Если PCAN-View на этом же подключении видит кадры, запомните его bitrate и используйте кнопку " +
                    "«ПРОВЕРИТЬ ВЫБРАННЫЙ BITRATE» — она полностью обходит автоматический Probe.",
                    MessageBoxImage.Warning);
                return;
            }

            LiveBitrateCombo.SelectedItem = bitrate;
            LiveConnectionText.Text = "CAN FOUND";
            LiveInstructionText.Text =
                $"Реальный CAN найден: {bitrate:N0} bit/s, принято {best.Frames:N0} тестовых кадров. После закрытия журнала подключаю постоянный Live-приём…";
            StatusText.Text = $"PCAN: CAN найден на {bitrate:N0} bit/s; тестовых кадров {best.Frames:N0}.";

            ShowPcanDiagnosticResult(
                $"CAN найден: {channel.DisplayName}, {bitrate:N0} bit/s.{Environment.NewLine}{Environment.NewLine}" +
                $"Результаты всех попыток:{Environment.NewLine}{report.DescribeDetailed()}{Environment.NewLine}{Environment.NewLine}" +
                "После OK CraneCAN откроет этот же канал в постоянном LISTEN ONLY.",
                MessageBoxImage.Information);

            MainTabs.SelectedIndex = 0;
            LiveConnectButton_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception)
        {
            ShowPcanDiagnosticResult(FormatException(exception), MessageBoxImage.Error);
        }
        finally
        {
            _pcanDiagnosticBusy = false;
            if (sourceElement is not null)
                sourceElement.IsEnabled = true;
            UpdateLiveControls();
        }
    }

    private async void DiagnoseKnownPcanBitrateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pcanDiagnosticBusy)
            return;

        if (_liveConnectionReady)
        {
            MessageBox.Show(
                "Live CAN уже подключён. Сначала отключите Live CAN, затем выполните отдельный тест выбранного bitrate.",
                "Проверка выбранного bitrate",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _pcanDiagnosticBusy = true;
        var sourceElement = sender as FrameworkElement;
        if (sourceElement is not null)
            sourceElement.IsEnabled = false;

        try
        {
            LiveSourceCombo.SelectedIndex = 1;
            var channel = LiveChannelCombo.SelectedItem as CanChannelDescriptor;
            if (channel is null)
            {
                await using var discovery = new PcanBasicCanDriver();
                var channels = await discovery.DiscoverChannelsAsync();
                if (channels.Count == 0)
                {
                    ShowPcanDiagnosticResult(
                        "Нет доступного PCAN-USB канала." + Environment.NewLine + Environment.NewLine +
                        FormatPcanTrace(discovery.GetOperationDiagnostics()),
                        MessageBoxImage.Warning);
                    return;
                }

                LiveChannelCombo.ItemsSource = channels;
                LiveChannelCombo.SelectedIndex = 0;
                channel = channels[0];
            }

            var bitrate = LiveBitrateCombo.SelectedItem is int selected ? selected : 250_000;
            StatusText.Text = $"PCAN: прямой тест {channel.DisplayName}, {bitrate:N0} bit/s…";
            LiveInstructionText.Text =
                "Прямой аппаратный тест: Auto-Probe отключён. Открываю только выбранный bitrate в LISTEN ONLY и слушаю до 5 секунд.";

            await using var driver = new PcanBasicCanDriver();
            await driver.OpenAsync(new CanChannelSettings(
                channel.Id, bitrate, ListenOnly: true, IncludeErrorFrames: true));

            long frames = 0;
            using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await foreach (var _ in driver.ReadFramesAsync(testCts.Token))
                {
                    frames++;
                    if (frames >= 64)
                        break;
                }
            }
            catch (OperationCanceledException) when (testCts.IsCancellationRequested)
            {
            }

            var status = driver.GetStatus();
            await driver.CloseAsync();
            var trace = FormatPcanTrace(driver.GetOperationDiagnostics());

            if (frames > 0 || status.ReceivedFrames > 0)
            {
                LiveConnectionText.Text = "TEST OK";
                LiveInstructionText.Text =
                    $"Выбранный bitrate подтверждён: {bitrate:N0} bit/s. Тест завершён и канал закрыт. Теперь нажмите «ПОДКЛЮЧИТЬ».";
                ShowPcanDiagnosticResult(
                    $"ПРЯМОЙ ТЕСТ УСПЕШЕН{Environment.NewLine}" +
                    $"Канал: {channel.DisplayName}{Environment.NewLine}" +
                    $"Bitrate: {bitrate:N0} bit/s{Environment.NewLine}" +
                    $"Кадров: {Math.Max(frames, status.ReceivedFrames):N0}{Environment.NewLine}" +
                    $"Lost: {status.LostFrames:N0}; Errors: {status.ErrorFrames:N0}{Environment.NewLine}{Environment.NewLine}" +
                    trace,
                    MessageBoxImage.Information);
            }
            else
            {
                LiveConnectionText.Text = "NO CAN FRAMES";
                LiveInstructionText.Text =
                    $"На выбранном bitrate {bitrate:N0} bit/s кадров нет. Сверьте bitrate с PCAN-View и физическое подключение.";
                ShowPcanDiagnosticResult(
                    $"ПРЯМОЙ ТЕСТ: КАДРОВ НЕТ{Environment.NewLine}" +
                    $"Канал: {channel.DisplayName}{Environment.NewLine}" +
                    $"Bitrate: {bitrate:N0} bit/s{Environment.NewLine}" +
                    $"Lost: {status.LostFrames:N0}; Errors: {status.ErrorFrames:N0}{Environment.NewLine}{Environment.NewLine}" +
                    trace,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowPcanDiagnosticResult(FormatException(exception), MessageBoxImage.Error);
        }
        finally
        {
            _pcanDiagnosticBusy = false;
            if (sourceElement is not null)
                sourceElement.IsEnabled = true;
            UpdateLiveControls();
        }
    }

    private static string FormatPcanTrace(IReadOnlyList<PcanOperationDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return "Аппаратный журнал PCAN пуст.";

        return string.Join(Environment.NewLine,
            diagnostics.Select(item =>
                $"{item.Timestamp:HH:mm:ss.fff}  {item.Operation}: {item.StatusText}"));
    }

    private void ShowPcanDiagnosticResult(string message, MessageBoxImage image)
    {
        StatusText.Text = message.Replace('\n', ' ');
        MessageBox.Show(message, "Проверка PCAN / Live CAN", MessageBoxButton.OK, image);
    }
}
