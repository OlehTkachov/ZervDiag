using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;

namespace CraneCAN.App;

public partial class MainWindow
{
    private void ShowIncidentEventChainWindow(
        IncidentTransitionAnalysisResult transition,
        Window owner)
    {
        var result = IncidentEventChainAnalyzer.Build(transition, _machineProfile);
        var warnings = result.Warnings.Count == 0
            ? "Предупреждения: нет."
            : "Предупреждения:\n• " + string.Join("\n• ", result.Warnings);

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Наблюдаемая цепочка: {result.Steps.Count:N0} шагов; " +
                $"с Machine Profile сопоставлено {result.ProfileAnnotatedCount:N0}; " +
                $"кандидатов на разрыв {result.BreakpointCount:N0}.\n" +
                warnings + "\n" +
                "Порядок шагов основан только на времени наблюдаемых CAN-изменений. " +
                "«Разрыв» означает остановку периодического ID или крупную временную паузу; " +
                "это не автоматическое доказательство места физической неисправности."
        };

        var rows = result.Steps
            .Select(step => new IncidentEventChainRow(step))
            .ToArray();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = rows
        };

        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(IncidentEventChainRow.Sequence)), Width = 45 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Время", Binding = new Binding(nameof(IncidentEventChainRow.TimeText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Δ предыдущий", Binding = new Binding(nameof(IncidentEventChainRow.DeltaText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(IncidentEventChainRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Формат", Binding = new Binding(nameof(IncidentEventChainRow.FormatText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Место", Binding = new Binding(nameof(IncidentEventChainRow.LocationText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Событие", Binding = new Binding(nameof(IncidentEventChainRow.KindText)), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Profile signal", Binding = new Binding(nameof(IncidentEventChainRow.ProfileSignalsText)), Width = 190 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Знание", Binding = new Binding(nameof(IncidentEventChainRow.ConfidenceText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Переход", Binding = new Binding(nameof(IncidentEventChainRow.TransitionText)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Разрыв?", Binding = new Binding(nameof(IncidentEventChainRow.BreakpointText)), Width = 210 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наблюдение",
            Binding = new Binding(nameof(IncidentEventChainRow.Description)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var addSignal = new Button
        {
            Content = "Добавить DATA-шаг в профиль…",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            IsEnabled = false,
            ToolTip = "Signal Builder доступен только для выбранного изменения DATA[n]. Новый сигнал добавляется как CANDIDATE."
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
        buttons.Children.Add(addSignal);
        buttons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = owner,
            Title = "CraneCAN — наблюдаемая цепочка событий",
            Width = 1380,
            Height = 700,
            MinWidth = 980,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };

        grid.SelectionChanged += (_, _) =>
        {
            addSignal.IsEnabled = grid.SelectedItem is IncidentEventChainRow row &&
                                  row.Step.Kind == IncidentTransitionKind.ByteChanged &&
                                  row.Step.DataIndex.HasValue;
        };
        addSignal.Click += (_, _) =>
        {
            if (grid.SelectedItem is IncidentEventChainRow row &&
                row.Step.Kind == IncidentTransitionKind.ByteChanged &&
                row.Step.DataIndex.HasValue)
            {
                ShowIncidentSignalBuilder(row.Step, transition.MarkerTime, window);
            }
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private sealed record IncidentEventChainRow(IncidentEventChainStep Step)
    {
        public int Sequence => Step.Sequence;
        public string TimeText => FormatEventChainTime(Step.ReactionMilliseconds);
        public string DeltaText => Step.DeltaFromPreviousMilliseconds.HasValue
            ? $"{Step.DeltaFromPreviousMilliseconds.Value:0.###} мс"
            : "—";
        public string IdText => Step.Id.ToString(
            Step.IsExtended ? "X8" : "X3",
            CultureInfo.InvariantCulture);
        public string FormatText => Step.IsExtended ? "Extended" : "Standard";
        public string LocationText => Step.DataIndex.HasValue
            ? $"DATA[{Step.DataIndex.Value}]"
            : "ID/DLC";
        public string KindText => Step.Kind switch
        {
            IncidentTransitionKind.IdAppeared => "ID появился",
            IncidentTransitionKind.PeriodicIdStopped => "ID прекратился",
            IncidentTransitionKind.DlcChanged => "DLC изменился",
            IncidentTransitionKind.ByteChanged => "байт изменился",
            _ => Step.Kind.ToString()
        };
        public string ProfileSignalsText => Step.ProfileSignals.Count == 0
            ? "—"
            : string.Join("; ", Step.ProfileSignals);
        public string ConfidenceText => Step.HighestSignalConfidence?.ToString().ToUpperInvariant() ?? "—";
        public string TransitionText => $"{Step.BaselineValue} → {Step.ObservedValue}";
        public string BreakpointText => Step.BreakpointCandidate
            ? Step.BreakpointReason
            : "—";
        public string Description => Step.Description;

        private static string FormatEventChainTime(double milliseconds)
        {
            var seconds = milliseconds / 1000.0;
            return seconds >= 0 ? $"+{seconds:0.000} s" : $"{seconds:0.000} s";
        }
    }
}
