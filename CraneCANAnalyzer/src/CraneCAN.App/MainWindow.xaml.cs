using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
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
    private readonly List<GuidedExperimentRun> _guidedRuns = [];
    private readonly List<GuidedExperimentRepeat> _guidedRepeatDefinitions = [];
    private IReadOnlyList<GuidedCandidate> _guidedCandidates = [];
    private MachineProfile _machineProfile = new();
    private string? _machineProfilePath;

    public MainWindow()
    {
        InitializeComponent();
        FormatFilterCombo.ItemsSource = new[] { "Все", "Standard", "Extended" };
        FormatFilterCombo.SelectedIndex = 0;
        FramesGrid.ItemsSource = _frameRows;
        StatisticsGrid.ItemsSource = Array.Empty<FrameStatistics>();
        ComparisonGrid.ItemsSource = Array.Empty<GenericCanComparisonRow>();
        GuidedCandidatesGrid.ItemsSource = Array.Empty<GuidedCandidateRow>();
        GuidedActionTypeCombo.ItemsSource = new[]
        {
            "сигнал кнопки", "сигнал джойстика", "состояние концевика", "состояние датчика",
            "разрешение", "блокировка", "команда реле", "команда клапана", "выход контроллера",
            "сигнал режима", "источник неисправности", "сравнение двух состояний", "пользовательское действие"
        };
        GuidedActionTypeCombo.SelectedIndex = 1;
        ProfileStatusCombo.ItemsSource = new[]
        {
            SignalKnowledgeState.Candidate,
            SignalKnowledgeState.Probable,
            SignalKnowledgeState.Confirmed
        };
        ProfileStatusCombo.SelectedIndex = 0;
        ApplyProfileToFields();
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
        GuidedReferenceStartTextBox.Text = "0";
        GuidedReferenceEndTextBox.Text = FormatMilliseconds(referenceEnd);
        GuidedActionStartTextBox.Text = FormatMilliseconds(referenceEnd);
        GuidedActionEndTextBox.Text = FormatMilliseconds(duration + 0.001);
        GuidedApproximateTimeTextBox.Text = FormatMilliseconds(referenceEnd);
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
        _guidedRuns.Clear();
        _guidedRepeatDefinitions.Clear();
        _guidedCandidates = [];
        FramesGrid.ItemsSource = _frameRows;
        StatisticsGrid.ItemsSource = Array.Empty<FrameStatistics>();
        ComparisonGrid.ItemsSource = Array.Empty<GenericCanComparisonRow>();
        GuidedCandidatesGrid.ItemsSource = Array.Empty<GuidedCandidateRow>();
        LoadedFileText.Text = "TRC не открыт";
        ComparisonSummaryText.Text = "Сравнение ещё не выполнено.";
        GuidedRepeatsText.Text = "Повторы ещё не добавлены.";
        GuidedQualityText.Text = "Качество эксперимента ещё не оценено.";
        GuidedCandidateDetailsText.Text = "Выберите кандидата, чтобы увидеть прозрачное объяснение score.";
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
            FileName = $"CraneCAN_comparison_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
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

    private void AddGuidedRepeatButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (run, definition) = CreateCurrentGuidedRepeat(_guidedRuns.Count + 1);
            _guidedRuns.Add(run);
            _guidedRepeatDefinitions.Add(definition);
            GuidedRepeatsText.Text = $"Добавлено повторов: {_guidedRuns.Count}. " +
                                     "Один случай не считается подтверждением; рекомендуется 3 повтора.";
            GuidedQualityText.Text = "Нажмите «АНАЛИЗИРОВАТЬ» или откройте следующий TRC и добавьте ещё один повтор.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка добавления повтора",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AnalyzeGuidedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_guidedRuns.Count == 0)
            {
                var (run, definition) = CreateCurrentGuidedRepeat(1);
                _guidedRuns.Add(run);
                _guidedRepeatDefinitions.Add(definition);
                GuidedRepeatsText.Text = "Добавлен повтор 1/1. Для подтверждения сигнала выполните дополнительные опыты.";
            }

            var actionName = GuidedActionNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(actionName))
            {
                throw new InvalidOperationException("Введите понятное название физического действия.");
            }

            SetBusy(true, "Анализ переходов и повторяемости…");
            var result = await Task.Run(() => GuidedDiagnosticsAnalyzer.Analyze(actionName, _guidedRuns));
            _guidedCandidates = result.Candidates;
            GuidedCandidatesGrid.ItemsSource = result.Candidates
                .Select((candidate, index) => new GuidedCandidateRow(index + 1, candidate))
                .ToArray();
            GuidedQualityText.Text = FormatQuality(result.Quality);
            GuidedCandidateDetailsText.Text = result.Quality.CanAnalyze
                ? $"Найдено кандидатов: {result.Candidates.Count}. Выберите строку для объяснения score."
                : "Уверенный вывод не сформирован: исправьте ошибки качества эксперимента.";
            StatusText.Text = result.Quality.CanAnalyze
                ? $"Guided Diagnostics: кандидатов {result.Candidates.Count}; повторов {_guidedRuns.Count}."
                : "Guided Diagnostics остановлен из-за непригодных входных данных.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка Guided Diagnostics",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private (GuidedExperimentRun Run, GuidedExperimentRepeat Definition) CreateCurrentGuidedRepeat(int repeatNumber)
    {
        if (_loadedFrames.Count == 0 || string.IsNullOrWhiteSpace(_loadedTrcPath))
        {
            throw new InvalidOperationException("Сначала откройте PCAN-View TRC с записью эксперимента.");
        }

        var referenceWindow = new TraceWindow(
            ParseMilliseconds(GuidedReferenceStartTextBox.Text, "начало REFERENCE"),
            ParseMilliseconds(GuidedReferenceEndTextBox.Text, "конец REFERENCE"));
        var actionWindow = new TraceWindow(
            ParseMilliseconds(GuidedActionStartTextBox.Text, "начало ACTION"),
            ParseMilliseconds(GuidedActionEndTextBox.Text, "конец ACTION"));
        referenceWindow.Validate();
        actionWindow.Validate();
        var origin = _loadedFrames.Min(frame => frame.Timestamp);
        var referenceFrames = SelectFrames(_loadedFrames, origin, referenceWindow);
        var actionFrames = SelectFrames(_loadedFrames, origin, actionWindow);
        var approximateMilliseconds = ParseOptionalMilliseconds(
            GuidedApproximateTimeTextBox.Text, "примерное время действия");
        var toleranceMilliseconds = ParseMilliseconds(GuidedToleranceTextBox.Text, "временной допуск");
        var bus = string.IsNullOrWhiteSpace(CanBusTextBox.Text) ? "CAN1" : CanBusTextBox.Text.Trim();

        var definition = new GuidedExperimentRepeat
        {
            RepeatNumber = repeatNumber,
            ReferenceSource = new ExperimentTraceSource
            {
                Path = _loadedTrcPath,
                Bus = bus,
                Window = referenceWindow
            },
            ActionSource = new ExperimentTraceSource
            {
                Path = _loadedTrcPath,
                Bus = bus,
                Window = actionWindow
            },
            ActionApproximateTimeMilliseconds = approximateMilliseconds,
            EventSearchToleranceMilliseconds = toleranceMilliseconds
        };
        var run = new GuidedExperimentRun(
            repeatNumber,
            bus,
            referenceFrames,
            actionFrames,
            approximateMilliseconds.HasValue ? origin.AddMilliseconds(approximateMilliseconds.Value) : null,
            TimeSpan.FromMilliseconds(toleranceMilliseconds),
            ReferenceWindow: referenceWindow,
            ActionWindow: actionWindow,
            ReferencePath: _loadedTrcPath,
            ActionPath: _loadedTrcPath);
        return (run, definition);
    }

    private void GuidedCandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GuidedCandidatesGrid.SelectedItem is not GuidedCandidateRow row)
        {
            return;
        }

        var reasons = row.Candidate.ScoreExplanation.Count == 0
            ? "нет положительных факторов"
            : string.Join("; ", row.Candidate.ScoreExplanation.Select(item =>
                $"{(item.Points >= 0 ? "+" : string.Empty)}{item.Points} {GuidedDiagnosticsText.Score(item)}"));
        var sequence = row.Candidate.Temporal?.Sequence.Count > 0
            ? " Последовательность: " + string.Join(" → ", row.Candidate.Temporal.Sequence.Select(value => value.ToString("X2"))) + "."
            : string.Empty;
        GuidedCandidateDetailsText.Text =
            $"Почему кандидат №{row.Rank}: {reasons}. Статус остаётся CANDIDATE до подтверждения.{sequence}";
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _machineProfile = new MachineProfile();
        _machineProfilePath = null;
        ApplyProfileToFields();
        StatusText.Text = "Создан профиль новой / неизвестной машины.";
    }

    private async void OpenProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Профиль CraneCAN (*.craneprofile)|*.craneprofile|JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _machineProfile = await GuidedJsonCodec.LoadProfileAsync(dialog.FileName);
            _machineProfilePath = dialog.FileName;
            ApplyProfileToFields();
            StatusText.Text = $"Профиль открыт: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка открытия профиля",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Профиль CraneCAN (*.craneprofile)|*.craneprofile",
            FileName = _machineProfilePath is null
                ? SanitizeFileName(MachineNameTextBox.Text) + ".craneprofile"
                : Path.GetFileName(_machineProfilePath)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            UpdateProfileFromFields();
            await GuidedJsonCodec.SaveProfileAsync(dialog.FileName, _machineProfile);
            _machineProfilePath = dialog.FileName;
            StatusText.Text = $"Профиль сохранён: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка сохранения профиля",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddCandidateToProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (GuidedCandidatesGrid.SelectedItem is not GuidedCandidateRow row)
        {
            MessageBox.Show("Выберите кандидата в таблице.", "Профиль машины",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var status = ProfileStatusCombo.SelectedItem is SignalKnowledgeState selected
            ? selected
            : SignalKnowledgeState.Candidate;
        if (status == SignalKnowledgeState.Confirmed && row.Candidate.RepeatabilityCount < 2)
        {
            var answer = MessageBox.Show(
                "Кандидат наблюдался только в одном опыте. CONFIRMED означает пользовательское подтверждение evidence. Продолжить?",
                "Подтверждение статуса",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        UpdateProfileFromFields();
        _machineProfile = MachineProfileService.AddCandidate(
            _machineProfile,
            row.Candidate,
            GuidedActionNameTextBox.Text.Trim(),
            status,
            $"Добавлено пользователем из кандидата №{row.Rank}");
        StatusText.Text = $"Сигнал добавлен в профиль как {status}. Сохраните профиль на диск.";
    }

    private async void SaveGuidedExperimentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_guidedRepeatDefinitions.Count == 0)
        {
            MessageBox.Show("Сначала добавьте хотя бы один REFERENCE/ACTION повтор.", "Эксперимент",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Эксперимент CraneCAN (*.canexperiment)|*.canexperiment",
            FileName = SanitizeFileName(GuidedActionNameTextBox.Text) + ".canexperiment"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var experiment = CreateExperimentDocument();
            await GuidedJsonCodec.SaveExperimentAsync(dialog.FileName, experiment);
            StatusText.Text = $"Эксперимент сохранён: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка сохранения эксперимента",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenGuidedExperimentButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Эксперимент CraneCAN (*.canexperiment)|*.canexperiment|JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetBusy(true, "Открытие эксперимента и связанных TRC…");
        try
        {
            var experiment = await GuidedJsonCodec.LoadExperimentAsync(dialog.FileName);
            var loadedRuns = new List<GuidedExperimentRun>();
            foreach (var definition in experiment.Repeats.OrderBy(item => item.RepeatNumber))
            {
                loadedRuns.Add(await LoadGuidedRunAsync(definition));
            }

            _guidedRuns.Clear();
            _guidedRuns.AddRange(loadedRuns);
            _guidedRepeatDefinitions.Clear();
            _guidedRepeatDefinitions.AddRange(experiment.Repeats);
            GuidedActionNameTextBox.Text = experiment.ActionName;
            CanBusTextBox.Text = experiment.Bus;
            GuidedRepeatsText.Text = $"Открыт эксперимент: повторов {_guidedRuns.Count}.";
            GuidedQualityText.Text = "Связанные TRC загружены. Нажмите «АНАЛИЗИРОВАТЬ».";
            StatusText.Text = $"Эксперимент открыт: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Ошибка открытия эксперимента",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private GuidedExperiment CreateExperimentDocument() => new()
    {
        MachineProfileId = _machineProfile.ProfileId,
        Name = GuidedActionNameTextBox.Text.Trim(),
        ActionName = GuidedActionNameTextBox.Text.Trim(),
        Description = GuidedActionTypeCombo.SelectedItem?.ToString() ?? string.Empty,
        Bus = string.IsNullOrWhiteSpace(CanBusTextBox.Text) ? "CAN1" : CanBusTextBox.Text.Trim(),
        Repeats = _guidedRepeatDefinitions.ToList(),
        Candidates = _guidedCandidates.Select(candidate => new GuidedCandidateSnapshot
        {
            StableKey = candidate.StableKey,
            Id = candidate.Id,
            IsExtended = candidate.IsExtended,
            DataIndex = candidate.DataIndex,
            BitIndex = candidate.BitIndex,
            ReferenceValue = candidate.ReferenceValue,
            ActionValue = candidate.ActionValue,
            Score = candidate.Score,
            RepeatabilityCount = candidate.RepeatabilityCount,
            RepeatCount = candidate.RepeatCount,
            Status = candidate.Status
        }).ToList()
    };

    private static async Task<GuidedExperimentRun> LoadGuidedRunAsync(GuidedExperimentRepeat definition)
    {
        if (definition.ReferenceSource.Window is null || definition.ActionSource.Window is null)
        {
            throw new InvalidDataException($"Повтор {definition.RepeatNumber} не содержит временных окон.");
        }

        var referenceTrace = await PcanTrcCodec.LoadAsync(
            definition.ReferenceSource.Path, definition.ReferenceSource.Channel);
        var actionTrace = string.Equals(
            definition.ReferenceSource.Path, definition.ActionSource.Path, StringComparison.OrdinalIgnoreCase)
            ? referenceTrace
            : await PcanTrcCodec.LoadAsync(definition.ActionSource.Path, definition.ActionSource.Channel);
        var referenceOrigin = referenceTrace.Min(frame => frame.Timestamp);
        var actionOrigin = actionTrace.Min(frame => frame.Timestamp);
        return new GuidedExperimentRun(
            definition.RepeatNumber,
            definition.ActionSource.Bus,
            SelectFrames(referenceTrace, referenceOrigin, definition.ReferenceSource.Window),
            SelectFrames(actionTrace, actionOrigin, definition.ActionSource.Window),
            definition.ActionApproximateTimeMilliseconds.HasValue
                ? actionOrigin.AddMilliseconds(definition.ActionApproximateTimeMilliseconds.Value)
                : null,
            TimeSpan.FromMilliseconds(definition.EventSearchToleranceMilliseconds),
            ReferenceWindow: definition.ReferenceSource.Window,
            ActionWindow: definition.ActionSource.Window,
            ReferencePath: definition.ReferenceSource.Path,
            ActionPath: definition.ActionSource.Path);
    }

    private void ApplyProfileToFields()
    {
        MachineNameTextBox.Text = _machineProfile.MachineName;
        ManufacturerTextBox.Text = string.IsNullOrWhiteSpace(_machineProfile.Manufacturer)
            ? "unknown"
            : _machineProfile.Manufacturer;
        MachineModelTextBox.Text = string.IsNullOrWhiteSpace(_machineProfile.Model)
            ? "unknown"
            : _machineProfile.Model;
        CanBusTextBox.Text = _machineProfile.CanBusName;
    }

    private void UpdateProfileFromFields()
    {
        _machineProfile = _machineProfile with
        {
            MachineName = string.IsNullOrWhiteSpace(MachineNameTextBox.Text)
                ? "Новая / неизвестная машина"
                : MachineNameTextBox.Text.Trim(),
            Manufacturer = ManufacturerTextBox.Text.Trim(),
            Model = MachineModelTextBox.Text.Trim(),
            CanBusName = string.IsNullOrWhiteSpace(CanBusTextBox.Text) ? "CAN1" : CanBusTextBox.Text.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static CanFrame[] SelectFrames(
        IReadOnlyList<CanFrame> frames,
        DateTimeOffset origin,
        TraceWindow window)
    {
        var start = origin.AddMilliseconds(window.StartMilliseconds);
        var end = origin.AddMilliseconds(window.EndMilliseconds);
        return frames.Where(frame => frame.Timestamp >= start && frame.Timestamp < end).ToArray();
    }

    private static string FormatQuality(ExperimentQualityReport quality)
    {
        var heading = !quality.CanAnalyze
            ? "Эксперимент непригоден:"
            : quality.IsGood ? "Эксперимент пригоден:" : "Эксперимент сомнительный:";
        return heading + " " + string.Join("; ", quality.Issues.Select(issue =>
        {
            var marker = issue.Severity switch
            {
                ExperimentQualitySeverity.Error => "✗",
                ExperimentQualitySeverity.Warning => "⚠",
                _ => "✓"
            };
            var repeat = issue.RepeatNumber.HasValue ? $" (повтор {issue.RepeatNumber})" : string.Empty;
            return $"{marker} {GuidedDiagnosticsText.Quality(issue)}{repeat}";
        }));
    }

    private static double? ParseOptionalMilliseconds(string text, string fieldName) =>
        string.IsNullOrWhiteSpace(text) ? null : ParseMilliseconds(text, fieldName);

    private static string SanitizeFileName(string value)
    {
        var fallback = string.IsNullOrWhiteSpace(value) ? "CraneCAN_experiment" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fallback = fallback.Replace(invalid, '_');
        }

        return fallback;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        OpenTrcButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        CompareWindowsButton.IsEnabled = !busy;
        CompareFilesButton.IsEnabled = !busy;
        AnalyzeGuidedButton.IsEnabled = !busy;
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

public sealed record GuidedCandidateRow
{
    public GuidedCandidateRow(int rank, GuidedCandidate candidate)
    {
        Rank = rank;
        Candidate = candidate;
        ActionName = candidate.ActionName;
        StatusText = candidate.Status.ToString().ToUpperInvariant();
        Interpretation = GuidedDiagnosticsText.Interpretation(candidate);
        ScoreText = $"{candidate.Score}/100";
        RepeatabilityText = $"{candidate.RepeatabilityCount}/{candidate.RepeatCount}";
        ReactionText = candidate.ReactionMilliseconds.HasValue
            ? $"{candidate.ReactionMilliseconds.Value:+0.###;-0.###;0} ms"
            : "—";
        AgreementText = $"{candidate.AgreementPercent:F1}%";
        IdText = candidate.IsExtended ? candidate.Id.ToString("X8") : candidate.Id.ToString("X3");
        LocationText = candidate.DataIndex.HasValue
            ? candidate.BitIndex.HasValue
                ? $"DATA[{candidate.DataIndex}], bit {candidate.BitIndex}"
                : $"DATA[{candidate.DataIndex}]"
            : "Сообщение";
        TransitionText = candidate.ReferenceValue.HasValue || candidate.ActionValue.HasValue
            ? $"{FormatByte(candidate.ReferenceValue)} → {FormatByte(candidate.ActionValue)}"
            : candidate.ChangeKind switch
            {
                CandidateChangeKind.MessageAppeared => "появилось",
                CandidateChangeKind.MessageDisappeared => "исчезло",
                _ => "—"
            };
    }

    public int Rank { get; }
    public GuidedCandidate Candidate { get; }
    public string ActionName { get; }
    public string StatusText { get; }
    public string Interpretation { get; }
    public string ScoreText { get; }
    public string RepeatabilityText { get; }
    public string ReactionText { get; }
    public string AgreementText { get; }
    public string IdText { get; }
    public string LocationText { get; }
    public string TransitionText { get; }

    private static string FormatByte(byte? value) => value.HasValue ? value.Value.ToString("X2") : "—";
}
