using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async Task AnalyzeIncidentTransitionAsync(LoadedIncidentPackage package, Window owner)
    {
        SetBusy(true, "Incident: поиск первых изменений около отметки…");
        try
        {
            var result = await Task.Run(() => IncidentTransitionAnalyzer.Analyze(package.Incident));
            StatusText.Text = result.Earliest is null
                ? "Incident: изменений относительно baseline в заданном окне не найдено."
                : $"Incident: первое наблюдаемое изменение {FormatReaction(result.Earliest.ReactionMilliseconds)}.";
            ShowIncidentTransitionWindow(package, result, owner);
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Incident — анализ изменений",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private void ShowIncidentTransitionWindow(LoadedIncidentPackage package, IncidentTransitionAnalysisResult result, Window owner)
    {
        var warnings = result.Warnings.Count == 0
            ? "Предупреждения качества: нет."
            : "Предупреждения качества:\n• " + string.Join("\n• ", result.Warnings);
        var earliest = result.Earliest is null
            ? "Наблюдаемых кандидатов не найдено."
            : $"Первый кандидат: {FormatReaction(result.Earliest.ReactionMilliseconds)} · {FormatIncidentId(result.Earliest)}.";

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Incident {package.Incident.IncidentId:N}\n" +
                $"Маркер: {result.MarkerTime.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}\n" +
                $"BASELINE: {RelativeSeconds(result.BaselineStart, result.MarkerTime)} … {RelativeSeconds(result.BaselineEnd, result.MarkerTime)} s · {result.BaselineFrameCount:N0} кадров\n" +
                $"SEARCH: {RelativeSeconds(result.SearchStart, result.MarkerTime)} … {RelativeSeconds(result.SearchEnd, result.MarkerTime)} s · {result.SearchFrameCount:N0} кадров\n" +
                $"Кандидаты: HIGH {result.HighCount}; MEDIUM {result.MediumCount}; INFO {result.InfoCount}. {earliest}\n" +
                warnings + "\n" +
                "Отрицательное время означает изменение до отметки оператора. Результат показывает наблюдаемую последовательность CAN, а не автоматически установленную причину неисправности."
        };

        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            EnableRowVirtualization = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            ItemsSource = result.Candidates.Select(candidate => new IncidentTransitionRow(candidate)).ToArray()
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Время", Binding = new Binding(nameof(IncidentTransitionRow.ReactionText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Приоритет", Binding = new Binding(nameof(IncidentTransitionRow.PriorityText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(IncidentTransitionRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Формат", Binding = new Binding(nameof(IncidentTransitionRow.FormatText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Место", Binding = new Binding(nameof(IncidentTransitionRow.LocationText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Тип", Binding = new Binding(nameof(IncidentTransitionRow.KindText)), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Baseline", Binding = new Binding(nameof(IncidentTransitionRow.BaselineText)), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Наблюдалось", Binding = new Binding(nameof(IncidentTransitionRow.ObservedText)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Согласие", Binding = new Binding(nameof(IncidentTransitionRow.AgreementText)), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Повтор", Binding = new Binding(nameof(IncidentTransitionRow.ConfirmationText)), Width = 75 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Интерпретация", Binding = new Binding(nameof(IncidentTransitionRow.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var chain = new Button
        {
            Content = "Цепочка событий",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4)
        };
        var close = new Button { Content = "Закрыть", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(chain);
        buttons.Children.Add(close);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0); Grid.SetRow(grid, 1); Grid.SetRow(buttons, 2);
        layout.Children.Add(summary); layout.Children.Add(grid); layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = owner, Title = "CraneCAN — Incident First Changes", Width = 1280, Height = 680,
            MinWidth = 940, MinHeight = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout
        };
        chain.Click += (_, _) => ShowIncidentEventChainWindow(result, window);
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string FormatIncidentId(IncidentTransitionCandidate candidate) =>
        candidate.Id.ToString(candidate.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture);

    private static string FormatReaction(double milliseconds)
    {
        var seconds = milliseconds / 1000.0;
        return seconds >= 0 ? $"+{seconds:0.000} s" : $"{seconds:0.000} s";
    }

    private static string RelativeSeconds(DateTimeOffset value, DateTimeOffset marker)
    {
        var seconds = (value - marker).TotalSeconds;
        return seconds >= 0 ? $"+{seconds:0.###}" : $"{seconds:0.###}";
    }

    private sealed record IncidentTransitionRow(IncidentTransitionCandidate Candidate)
    {
        public string ReactionText => FormatReaction(Candidate.ReactionMilliseconds);
        public string PriorityText => Candidate.Priority switch
        {
            IncidentTransitionPriority.High => "HIGH",
            IncidentTransitionPriority.Medium => "MEDIUM",
            _ => "INFO"
        };
        public string IdText => FormatIncidentId(Candidate);
        public string FormatText => Candidate.IsExtended ? "Extended" : "Standard";
        public string LocationText => Candidate.DataIndex.HasValue ? $"DATA[{Candidate.DataIndex.Value}]" : "ID/DLC";
        public string KindText => Candidate.Kind switch
        {
            IncidentTransitionKind.IdAppeared => "ID появился",
            IncidentTransitionKind.PeriodicIdStopped => "ID прекратился",
            IncidentTransitionKind.DlcChanged => "DLC изменился",
            IncidentTransitionKind.ByteChanged => "байт изменился",
            _ => Candidate.Kind.ToString()
        };
        public string BaselineText => Candidate.BaselineValue;
        public string ObservedText => Candidate.ObservedValue;
        public string AgreementText => $"{Candidate.BaselineAgreementPercent:0.#}%";
        public string ConfirmationText => Candidate.ConfirmationCount.ToString(CultureInfo.InvariantCulture);
        public string Description => Candidate.Description;
    }
}
