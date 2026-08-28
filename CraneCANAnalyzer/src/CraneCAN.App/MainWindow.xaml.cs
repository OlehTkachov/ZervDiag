using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow : Window
{
    private IReadOnlyList<CanFrame> _loadedFrames = [];
    private IReadOnlyList<GenericCanFrameRow> _frameRows = [];
    private IReadOnlyList<GenericCanComparison> _allComparisons = [];
    private string? _loadedTrcPath;
    private string? _referenceTrcPath;
    private string? _actionTrcPath;
    private int _referenceFrameCount;
    private int _actionFrameCount;

    public MainWindow()
    {
        InitializeComponent();
        FormatFilterCombo.ItemsSource = new[] { "Все", "Standard", "Extended" };
        FormatFilterCombo.SelectedIndex = 0;
        FramesGrid.ItemsSource = _frameRows;
        StatisticsGrid.ItemsSource = Array.Empty<FrameStatistics>();
        ComparisonGrid.ItemsSource = Array.Empty<GenericCanComparisonRow>();
        LoadFieldGuide();
    }

    private async void OpenTrcButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateTrcOpenDialog();
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetBusy(true, "Чтение PCAN-View TRC…");
        try
        {
            var import = await PcanTrcCodec.LoadWithDiagnosticsAsync(dialog.FileName);
            _loadedTrcPath = dialog.FileName;
            _loadedFrames = import.Frames;
            BuildFrameRows();
            BuildStatistics();
            SetDefaultWindows();
            LoadedFileText.Text = dialog.FileName;
            StatusText.Text =
                $"Открыт TRC: {import.Frames.Count:N0} кадров; " +
                $"RTR пропущено: {import.RemoteFramesSkipped:N0}; error/status: {import.ErrorFramesSkipped:N0}; " +
                $"неизвестных строк: {import.UnknownOrMalformedLines:N0}. Передача CAN отсутствует.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка импорта TRC",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "TRC не открыт. Передача CAN отсутствует.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BuildFrameRows()
    {
        if (_loadedFrames.Count == 0)
        {
            _frameRows = [];
            FramesGrid.ItemsSource = _frameRows;
            FrameCountText.Text = "Кадров: 0";
            return;
        }

        var origin = _loadedFrames[0].Timestamp;
        DateTimeOffset? previous = null;
        var rows = new List<GenericCanFrameRow>(_loadedFrames.Count);
        foreach (var frame in _loadedFrames)
        {
            var delta = previous.HasValue
                ? (frame.Timestamp - previous.Value).TotalMilliseconds
                : (double?)null;
            rows.Add(new GenericCanFrameRow(frame, (frame.Timestamp - origin).TotalMilliseconds, delta));
            previous = frame.Timestamp;
        }

        _frameRows = rows;
        FramesGrid.ItemsSource = _frameRows;
        ApplyFrameFilter();
    }

    private void BuildStatistics()
    {
        var aggregator = new FrameStatisticsAggregator();
        foreach (var frame in _loadedFrames)
        {
            aggregator.Add(frame);
        }

        StatisticsGrid.ItemsSource = aggregator.Snapshot();
    }

    private void SetDefaultWindows()
    {
        if (_loadedFrames.Count == 0)
        {
            return;
        }

        var duration = Math.Max(1, (_loadedFrames[^1].Timestamp - _loadedFrames[0].Timestamp).TotalMilliseconds);
        var referenceEnd = Math.Min(15_000, Math.Max(1, duration * 0.5));
        ReferenceStartTextBox.Text = "0";
        ReferenceEndTextBox.Text = FormatMilliseconds(referenceEnd);
        ActionStartTextBox.Text = FormatMilliseconds(referenceEnd);
        ActionEndTextBox.Text = FormatMilliseconds(duration + 0.001);
    }

    private void FrameFilter_Changed(object sender, EventArgs e) => ApplyFrameFilter();

    private void ApplyFrameFilter()
    {
        if (FramesGrid.ItemsSource is null)
        {
            return;
        }

        var format = FormatFilterCombo.SelectedItem as string ?? "Все";
        var idTokens = ParseIdFilterTokens(IdFilterTextBox.Text);
        var view = CollectionViewSource.GetDefaultView(FramesGrid.ItemsSource);
        view.Filter = item => item is GenericCanFrameRow row &&
                              (format == "Все" || row.FormatText == format) &&
                              MatchesAnyId(row, idTokens);
        view.Refresh();
        FrameCountText.Text = idTokens.Count == 0 && format == "Все"
            ? $"Кадров: {_frameRows.Count:N0}"
            : $"Показано: {view.Cast<object>().Count():N0} из {_frameRows.Count:N0}";
    }

    private static IReadOnlyList<string> ParseIdFilterTokens(string text) => text
        .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(token => token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token)
        .Where(token => token.Length > 0)
        .ToArray();

    private static bool MatchesAnyId(GenericCanFrameRow row, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            {
                if (row.Id == id)
                {
                    return true;
                }
            }
            else if (row.IdText.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _loadedFrames = [];
        _frameRows = [];
        _allComparisons = [];
        _loadedTrcPath = null;
        _referenceFrameCount = 0;
        _actionFrameCount = 0;
        FramesGrid.ItemsSource = _frameRows;
        StatisticsGrid.ItemsSource = Array.Empty<FrameStatistics>();
        ComparisonGrid.ItemsSource = Array.Empty<GenericCanComparisonRow>();
        LoadedFileText.Text = "TRC не открыт";
        ComparisonSummaryText.Text = "Сравнение ещё не выполнено.";
        SaveComparisonButton.IsEnabled = false;
        FrameCountText.Text = "Кадров: 0";
        StatusText.Text = "Offline-анализ TRC. Передача CAN архитектурно отсутствует.";
    }

    private async void CompareWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedFrames.Count == 0)
        {
            MessageBox.Show("Сначала откройте исходный PCAN-View TRC.", "REFERENCE → ACTION",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var referenceWindow = new TraceWindow(
                ParseMilliseconds(ReferenceStartTextBox.Text, "начало REFERENCE"),
                ParseMilliseconds(ReferenceEndTextBox.Text, "конец REFERENCE"));
            var actionWindow = new TraceWindow(
                ParseMilliseconds(ActionStartTextBox.Text, "начало ACTION"),
                ParseMilliseconds(ActionEndTextBox.Text, "конец ACTION"));

            SetBusy(true, "Сравнение временных окон…");
            var result = await Task.Run(() =>
                GenericTraceExperiment.CompareWindows(_loadedFrames, referenceWindow, actionWindow));
            DisplayComparison(result);
            StatusText.Text = $"Сравнены окна файла {_loadedTrcPath}. Передача CAN отсутствует.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка сравнения окон",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SelectReferenceFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateTrcOpenDialog();
        if (dialog.ShowDialog(this) == true)
        {
            _referenceTrcPath = dialog.FileName;
            ReferenceFileText.Text = dialog.FileName;
        }
    }

    private void SelectActionFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateTrcOpenDialog();
        if (dialog.ShowDialog(this) == true)
        {
            _actionTrcPath = dialog.FileName;
            ActionFileText.Text = dialog.FileName;
        }
    }

    private async void CompareFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_referenceTrcPath) || string.IsNullOrWhiteSpace(_actionTrcPath))
        {
            MessageBox.Show("Выберите оба файла: REFERENCE TRC и ACTION TRC.", "REFERENCE → ACTION",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Чтение и сравнение двух TRC…");
        try
        {
            var result = await GenericTraceExperiment.CompareFilesAsync(_referenceTrcPath, _actionTrcPath);
            DisplayComparison(result);
            StatusText.Text = "REFERENCE и ACTION сравнены. Передача CAN отсутствует.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка сравнения файлов",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void DisplayComparison(GenericTraceExperimentResult result)
    {
        _referenceFrameCount = result.ReferenceFrameCount;
        _actionFrameCount = result.ActionFrameCount;
        _allComparisons = result.Comparisons;
        RefreshComparisonFilter();
        var changed = _allComparisons.Count(row => row.IsChanged);
        var significant = _allComparisons.Count(row => row.IsSignificant);
        var noisy = _allComparisons.Count(row => row.Kind == GenericCanChangeKind.UnstableOrAnalogNoise);
        var appeared = _allComparisons.Count(row => row.MessageAppeared);
        var disappeared = _allComparisons.Count(row => row.MessageDisappeared);
        ComparisonSummaryText.Text =
            $"Кадры REFERENCE/ACTION: {_referenceFrameCount:N0}/{_actionFrameCount:N0}. " +
            $"Изменений: {changed:N0}; значимых: {significant:N0}; нестабильных/шумовых: {noisy:N0}; " +
            $"ID появилось/исчезло: {appeared:N0}/{disappeared:N0}.";
        SaveComparisonButton.IsEnabled = true;
    }

    private void ComparisonFilter_Changed(object sender, RoutedEventArgs e) => RefreshComparisonFilter();

    private void RefreshComparisonFilter()
    {
        if (ComparisonGrid is null)
        {
            return;
        }

        var significantOnly = OnlySignificantCheckBox.IsChecked == true;
        ComparisonGrid.ItemsSource = _allComparisons
            .Where(row => !significantOnly || row.IsSignificant)
            .Select(row => new GenericCanComparisonRow(row))
            .ToArray();
    }

    private async void SaveComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allComparisons.Count == 0)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Отчёт сравнения CSV (*.csv)|*.csv",
            FileName = $"CraneCAN_SOOSAN_comparison_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await SaveComparisonCsvAsync(dialog.FileName);
            StatusText.Text = $"Полный результат сохранён: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка сохранения CSV",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveComparisonCsvAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Параметр;Значение");
        await writer.WriteLineAsync($"Дата UTC;{DateTimeOffset.UtcNow:O}");
        await writer.WriteLineAsync($"Кадров REFERENCE;{_referenceFrameCount}");
        await writer.WriteLineAsync($"Кадров ACTION;{_actionFrameCount}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync(
            "Приоритет;ID;Формат;Поле;REFERENCE;ACTION;XOR;Изменившиеся биты;" +
            "Образцы REFERENCE;Образцы ACTION;Стабильность REFERENCE, %;" +
            "Стабильность ACTION, %;Классификация;Значимое");

        foreach (var item in _allComparisons.Select(row => new GenericCanComparisonRow(row)))
        {
            var values = new[]
            {
                item.PriorityText, item.IdText, item.FormatText, item.Field,
                item.ReferenceHex, item.ActionHex, item.XorHex, item.ChangedBits,
                item.ReferenceSampleCount.ToString(CultureInfo.InvariantCulture),
                item.ActionSampleCount.ToString(CultureInfo.InvariantCulture),
                item.ReferenceAgreement.ToString("F1", CultureInfo.InvariantCulture),
                item.ActionAgreement.ToString("F1", CultureInfo.InvariantCulture),
                item.Classification, item.IsSignificant ? "да" : "нет"
            };
            await writer.WriteLineAsync(string.Join(';', values.Select(EscapeCsv)));
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        OpenTrcButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        CompareWindowsButton.IsEnabled = !busy;
        CompareFilesButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }
    }

    private void LoadFieldGuide()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "SOOSAN_FIELD_CAPTURE.md");
        FieldGuideTextBox.Text = File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : "Файл docs\\SOOSAN_FIELD_CAPTURE.md не найден. Используйте README из папки публикации.";
    }

    private static OpenFileDialog CreateTrcOpenDialog() => new()
    {
        Filter = "PCAN-View Trace (*.trc)|*.trc|Все файлы (*.*)|*.*",
        CheckFileExists = true,
        Multiselect = false
    };

    private static double ParseMilliseconds(string text, string fieldName)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new FormatException($"Поле «{fieldName}» должно содержать неотрицательное число миллисекунд.");
        }

        return value;
    }

    private static string FormatMilliseconds(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(';') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

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
}

public sealed record GenericCanFrameRow
{
    public GenericCanFrameRow(CanFrame frame, double offsetMilliseconds, double? deltaMilliseconds)
    {
        Id = frame.Id;
        TimeText = frame.Timestamp.ToString("HH:mm:ss.fffffff");
        OffsetText = offsetMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
        DeltaText = deltaMilliseconds?.ToString("F3", CultureInfo.InvariantCulture) ?? "—";
        Direction = frame.Direction.ToString();
        IdText = frame.IsExtended ? frame.Id.ToString("X8") : frame.Id.ToString("X3");
        FormatText = frame.IsExtended ? "Extended" : "Standard";
        Dlc = frame.Dlc;
        DataText = frame.DataText;
    }

    public uint Id { get; }
    public string TimeText { get; }
    public string OffsetText { get; }
    public string DeltaText { get; }
    public string Direction { get; }
    public string IdText { get; }
    public string FormatText { get; }
    public int Dlc { get; }
    public string DataText { get; }
}

public sealed record GenericCanComparisonRow
{
    public GenericCanComparisonRow(GenericCanComparison comparison)
    {
        PriorityKey = comparison.Priority.ToString();
        PriorityText = comparison.Priority switch
        {
            GenericCanChangePriority.VeryHigh => "Очень высокий",
            GenericCanChangePriority.High => "Высокий",
            GenericCanChangePriority.Medium => "Средний",
            GenericCanChangePriority.Low => "Низкий",
            _ => "—"
        };
        IdText = comparison.IsExtended ? comparison.Id.ToString("X8") : comparison.Id.ToString("X3");
        FormatText = comparison.IsExtended ? "Extended" : "Standard";
        Field = comparison.Field;
        ReferenceHex = FormatByte(comparison.ReferenceValue);
        ActionHex = FormatByte(comparison.ActionValue);
        XorHex = FormatByte(comparison.XorMask);
        ChangedBits = comparison.ChangedBits;
        ReferenceSampleCount = comparison.ReferenceSampleCount;
        ActionSampleCount = comparison.ActionSampleCount;
        SampleText = $"{ReferenceSampleCount:N0} / {ActionSampleCount:N0}";
        ReferenceAgreement = comparison.ReferenceAgreementPercent;
        ActionAgreement = comparison.ActionAgreementPercent;
        StabilityText = comparison.DataIndex.HasValue
            ? $"{ReferenceAgreement:F1}% / {ActionAgreement:F1}%"
            : "—";
        Classification = comparison.Classification;
        IsSignificant = comparison.IsSignificant;
    }

    public string PriorityKey { get; }
    public string PriorityText { get; }
    public string IdText { get; }
    public string FormatText { get; }
    public string Field { get; }
    public string ReferenceHex { get; }
    public string ActionHex { get; }
    public string XorHex { get; }
    public string ChangedBits { get; }
    public int ReferenceSampleCount { get; }
    public int ActionSampleCount { get; }
    public string SampleText { get; }
    public double ReferenceAgreement { get; }
    public double ActionAgreement { get; }
    public string StabilityText { get; }
    public string Classification { get; }
    public bool IsSignificant { get; }

    private static string FormatByte(byte? value) => value.HasValue ? value.Value.ToString("X2") : "—";
}
