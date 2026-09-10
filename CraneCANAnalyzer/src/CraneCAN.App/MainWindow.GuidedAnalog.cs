using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Profiles;

namespace CraneCAN.App;

public partial class MainWindow
{
    private Button? _guidedAnalogButton;

    [ModuleInitializer]
    internal static void RegisterGuidedAnalogUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(GuidedAnalogMainWindowLoaded));
    }

    private static void GuidedAnalogMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window ||
            window.AnalyzeGuidedButton.Parent is not Panel panel)
            return;

        if (panel.Children
            .OfType<Button>()
            .Any(button => string.Equals(
                button.Tag as string,
                "guided-analog-search",
                StringComparison.Ordinal)))
            return;

        var button = new Button
        {
            Content = "Аналоговые сигналы…",
            Tag = "guided-analog-search",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip =
                "Пассивный поиск sensor-like raw U8/U16 полей по REFERENCE/ACTION: " +
                "диапазон, шум, монотонность, corr(time), скорость, насыщение и повторяемость. CAN Tx отсутствует."
        };
        button.Click += window.GuidedAnalogButton_Click;

        var index = panel.Children.IndexOf(window.AnalyzeGuidedButton);
        if (index < 0) panel.Children.Add(button);
        else panel.Children.Insert(index, button);
        window._guidedAnalogButton = button;
    }

    private async void GuidedAnalogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_guidedRuns.Count == 0)
            {
                var (run, definition) = CreateCurrentGuidedRepeat(1);
                _guidedRuns.Add(run);
                _guidedRepeatDefinitions.Add(definition);
                GuidedRepeatsText.Text =
                    "Для Analog Search автоматически добавлен текущий повтор 1/1. " +
                    "Для HIGH-кандидата выполните минимум 3 одинаковых опыта.";
            }

            var actionName = GuidedActionNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(actionName))
                throw new InvalidOperationException("Введите понятное название физического действия.");

            if (_guidedAnalogButton is not null)
                _guidedAnalogButton.IsEnabled = false;
            SetBusy(true, "Guided Analog: поиск sensor-like U8/U16 полей…");

            var result = await Task.Run(() =>
                GuidedAnalogSignalAnalyzer.Analyze(actionName, _guidedRuns));
            ShowGuidedAnalogWindow(result);

            StatusText.Text =
                $"Guided Analog: кандидатов {result.Candidates.Count}; HIGH {result.HighCount}; " +
                $"MEDIUM {result.MediumCount}; повторов {result.RepeatCount}. CAN Tx отсутствует.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Guided Analog Signal Search",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            if (_guidedAnalogButton is not null)
                _guidedAnalogButton.IsEnabled = true;
        }
    }

    private void ShowGuidedAnalogWindow(GuidedAnalogAnalysisResult result)
    {
        var warnings = result.Warnings.Count == 0
            ? "Предупреждения: нет."
            : "Предупреждения:\n• " + string.Join("\n• ", result.Warnings);

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Действие: {result.ActionName}. Повторов: {result.RepeatCount}. " +
                $"Кандидатов: {result.Candidates.Count}; HIGH {result.HighCount}; " +
                $"MEDIUM {result.MediumCount}; LOW {result.LowCount}.\n" +
                warnings + "\n" +
                "HIGH означает только сильную повторяемую связь raw поля с ходом ACTION. " +
                "Это не подтверждение, что поле является длиной, углом, давлением или другим конкретным датчиком."
        };

        var rows = result.Candidates
            .Select((candidate, index) => new GuidedAnalogRow(index + 1, candidate))
            .ToArray();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = rows
        };

        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(GuidedAnalogRow.Rank)), Width = 42 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Приоритет", Binding = new Binding(nameof(GuidedAnalogRow.PriorityText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Score", Binding = new Binding(nameof(GuidedAnalogRow.ScoreText)), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(GuidedAnalogRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Формат", Binding = new Binding(nameof(GuidedAnalogRow.FormatText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Raw поле", Binding = new Binding(nameof(GuidedAnalogRow.FieldText)), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Направление", Binding = new Binding(nameof(GuidedAnalogRow.DirectionText)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Повторы", Binding = new Binding(nameof(GuidedAnalogRow.RepeatabilityText)), Width = 75 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Δ baseline", Binding = new Binding(nameof(GuidedAnalogRow.ShiftText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ACTION span", Binding = new Binding(nameof(GuidedAnalogRow.SpanText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ref noise", Binding = new Binding(nameof(GuidedAnalogRow.NoiseText)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "S/N", Binding = new Binding(nameof(GuidedAnalogRow.ResponseNoiseText)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Монотон.", Binding = new Binding(nameof(GuidedAnalogRow.MonotonicityText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "|corr(time)|", Binding = new Binding(nameof(GuidedAnalogRow.CorrelationText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "raw/s", Binding = new Binding(nameof(GuidedAnalogRow.RateText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Реакция", Binding = new Binding(nameof(GuidedAnalogRow.ReactionText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Плато", Binding = new Binding(nameof(GuidedAnalogRow.PlateauText)), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Возврат", Binding = new Binding(nameof(GuidedAnalogRow.ReturnText)), Width = 75 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Почему",
            Binding = new Binding(nameof(GuidedAnalogRow.ReasonsText)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var details = new TextBlock
        {
            Text =
                "Выберите строку. Добавление в Machine Profile создаёт только raw CANDIDATE " +
                "с Scale=1, Offset=0 и пустой Unit.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var add = new Button
        {
            Content = "Добавить raw-кандидат в профиль",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = false
        };
        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(add);
        buttons.Children.Add(close);

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(details);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(footer, 2);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(footer);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — Guided Analog Signal Search",
            Width = 1680,
            Height = 760,
            MinWidth = 1080,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not GuidedAnalogRow row)
            {
                add.IsEnabled = false;
                return;
            }

            add.IsEnabled = true;
            details.Text =
                $"{row.FieldText}: {row.Candidate.Direction}; score {row.Candidate.Score}; " +
                $"repeatability {row.Candidate.RepeatabilityCount}/{row.Candidate.RepeatCount}. " +
                $"Raw-кандидат не получает signed/scale/offset/unit автоматически.";
        };

        add.Click += (_, _) =>
        {
            if (grid.SelectedItem is GuidedAnalogRow row)
                AddGuidedAnalogCandidateToProfile(row.Candidate, window);
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private void AddGuidedAnalogCandidateToProfile(
        GuidedAnalogCandidate candidate,
        Window owner)
    {
        UpdateProfileFromFields();

        var existing = _machineProfile.KnownSignals
            .Concat(_machineProfile.ExperimentalSignals)
            .FirstOrDefault(signal =>
                signal.CanId == candidate.Id &&
                signal.IsExtended == candidate.IsExtended &&
                signal.StartByte == candidate.StartByte &&
                signal.StartBit == candidate.StartBit &&
                signal.BitLength == candidate.BitLength &&
                signal.ByteOrder == candidate.ByteOrder);

        if (existing is not null)
        {
            MessageBox.Show(
                $"Такое поле уже есть в Machine Profile как «{existing.Name}» ({existing.Confidence}). " +
                "Analog Search не перезаписывает существующее определение.",
                "Machine Profile",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var signal = GuidedAnalogSignalAnalyzer.CreateMachineSignal(
            candidate,
            _guidedDocument.ExperimentId,
            DateTimeOffset.UtcNow);
        _machineProfile.ExperimentalSignals.Add(signal);
        ApplyProfileToFields();

        StatusText.Text =
            $"В Machine Profile добавлен raw CANDIDATE «{signal.Name}». " +
            "Профиль ещё не сохранён. Scale/unit/физический смысл требуют проверки. CAN Tx отсутствует.";

        MessageBox.Show(
            "Raw-кандидат добавлен как CANDIDATE.\n\n" +
            "Автоматически НЕ назначены: signedness, scale, offset, unit и физический смысл. " +
            "Перед переводом в PROBABLE/CONFIRMED требуется независимое evidence.",
            "Machine Profile",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private sealed record GuidedAnalogRow(
        int Rank,
        GuidedAnalogCandidate Candidate)
    {
        public string PriorityText => Candidate.Priority.ToString().ToUpperInvariant();
        public string ScoreText => Candidate.Score.ToString(CultureInfo.InvariantCulture);
        public string IdText => Candidate.Id.ToString(
            Candidate.IsExtended ? "X8" : "X3",
            CultureInfo.InvariantCulture);
        public string FormatText => Candidate.IsExtended ? "Extended" : "Standard";
        public string FieldText => Candidate.Encoding switch
        {
            AnalogFieldEncoding.ByteUnsigned => $"DATA[{Candidate.StartByte}] U8",
            AnalogFieldEncoding.UInt16LittleEndian =>
                $"DATA[{Candidate.StartByte}..{Candidate.StartByte + 1}] U16 LE",
            AnalogFieldEncoding.UInt16BigEndian =>
                $"DATA[{Candidate.StartByte}..{Candidate.StartByte + 1}] U16 BE",
            _ => Candidate.Encoding.ToString()
        };
        public string DirectionText => Candidate.Direction == AnalogDirection.Increasing ? "растёт" : "падает";
        public string RepeatabilityText => $"{Candidate.RepeatabilityCount}/{Candidate.RepeatCount}";
        public string ShiftText => FormatSigned(Candidate.MedianBaselineShift);
        public string SpanText => FormatNumber(Candidate.MedianActionSpan);
        public string NoiseText => FormatNumber(Candidate.MedianReferenceNoise);
        public string ResponseNoiseText => FormatNumber(Candidate.MedianResponseToNoiseRatio);
        public string MonotonicityText => $"{Candidate.MedianMonotonicityPercent:0.#}%";
        public string CorrelationText => Candidate.MedianAbsoluteTimeCorrelation.ToString("0.###", CultureInfo.InvariantCulture);
        public string RateText => FormatSigned(Candidate.MedianRatePerSecond);
        public string ReactionText => Candidate.MedianReactionMilliseconds.HasValue
            ? $"{Candidate.MedianReactionMilliseconds.Value:0.#} ms"
            : "—";
        public string PlateauText => $"{Candidate.MedianEndpointPlateauPercent:0.#}%";
        public string ReturnText => Candidate.ReturnObservedCount == 0
            ? "—"
            : $"{Candidate.ReturnToBaselineCount}/{Candidate.ReturnObservedCount}";
        public string ReasonsText => string.Join("; ", Candidate.Reasons);

        private static string FormatSigned(double value) =>
            value.ToString(value >= 0 ? "+0.###" : "0.###", CultureInfo.InvariantCulture);

        private static string FormatNumber(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
