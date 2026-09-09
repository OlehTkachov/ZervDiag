using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async void FirstDivergenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_referenceTrcPath) || string.IsNullOrWhiteSpace(_actionTrcPath))
        {
            MessageBox.Show(
                "Выберите оба файла в блоке «Вариант B»: REFERENCE используется как GOOD, ACTION — как FAULT.",
                "GOOD / FAULT — первое расхождение",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "GOOD/FAULT: поиск первого расхождения…");
        try
        {
            var result = await FirstDivergenceAnalyzer.AnalyzeFilesAsync(_referenceTrcPath, _actionTrcPath);
            StatusText.Text = result.Earliest is null
                ? "GOOD/FAULT: явного DATA/DLC расхождения в сопоставленных кадрах не найдено."
                : $"GOOD/FAULT: первое наблюдаемое расхождение около +{result.Earliest.EvidenceOffsetMilliseconds:0.###} мс.";
            ShowFirstDivergenceWindow(result);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "GOOD / FAULT — первое расхождение",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowFirstDivergenceWindow(FirstDivergenceResult result)
    {
        var earliestText = result.Earliest is null
            ? "Явного DATA/DLC расхождения либо ID, присутствующего только в одной записи, не найдено."
            : $"Самый ранний кандидат: +{result.Earliest.EvidenceOffsetMilliseconds:0.###} мс, ID {FormatId(result.Earliest)}.";

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                "REFERENCE трактуется как GOOD, ACTION — как FAULT только для этого режима.\n" +
                $"GOOD/FAULT кадров: {result.GoodFrameCount:N0}/{result.FaultFrameCount:N0}. " +
                $"Сопоставлено пар: {result.MatchedFramePairs:N0}; вне допуска: " +
                $"GOOD {result.UnmatchedGoodFrames:N0}, FAULT {result.UnmatchedFaultFrames:N0}. " +
                $"Допуск времени: ±{result.MatchTolerance.TotalMilliseconds:0.###} мс.\n" +
                earliestText + "\n" +
                "Это наблюдаемое первое расхождение двух записей, а не автоматическое доказательство причины неисправности."
        };

        var rows = result.Candidates.Select(candidate => new FirstDivergenceRow(candidate)).ToArray();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            ItemsSource = rows
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Время", Binding = new Binding(nameof(FirstDivergenceRow.TimeText)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding(nameof(FirstDivergenceRow.IdText)), Width = 105 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Формат", Binding = new Binding(nameof(FirstDivergenceRow.FormatText)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Тип", Binding = new Binding(nameof(FirstDivergenceRow.KindText)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "GOOD", Binding = new Binding(nameof(FirstDivergenceRow.GoodText)), Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = "FAULT", Binding = new Binding(nameof(FirstDivergenceRow.FaultText)), Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Δt", Binding = new Binding(nameof(FirstDivergenceRow.DeltaText)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наблюдение",
            Binding = new Binding(nameof(FirstDivergenceRow.Description)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(close, 2);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(close);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — GOOD / FAULT First Divergence",
            Width = 1180,
            Height = 620,
            MinWidth = 880,
            MinHeight = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static string FormatId(FirstDivergenceCandidate candidate) =>
        candidate.Id.ToString(candidate.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture);

    private sealed record FirstDivergenceRow(FirstDivergenceCandidate Candidate)
    {
        public string TimeText => $"+{Candidate.EvidenceOffsetMilliseconds:0.###} мс";
        public string IdText => FormatId(Candidate);
        public string FormatText => Candidate.IsExtended ? "Extended" : "Standard";
        public string KindText => Candidate.Kind switch
        {
            FirstDivergenceKind.PayloadChanged => "DATA отличается",
            FirstDivergenceKind.DlcChanged => "DLC отличается",
            FirstDivergenceKind.IdOnlyInGood => "ID только в GOOD",
            FirstDivergenceKind.IdOnlyInFault => "ID только в FAULT",
            _ => Candidate.Kind.ToString()
        };
        public string GoodText => FormatSide(Candidate.GoodOffsetMilliseconds, Candidate.GoodData);
        public string FaultText => FormatSide(Candidate.FaultOffsetMilliseconds, Candidate.FaultData);
        public string DeltaText => Candidate.MatchDeltaMilliseconds.HasValue
            ? $"{Candidate.MatchDeltaMilliseconds.Value:0.###} мс"
            : "—";
        public string Description => Candidate.Description;

        private static string FormatSide(double? offset, byte[]? data)
        {
            if (!offset.HasValue) return "—";
            var payload = data is null ? "—" : string.Join(" ", data.Select(value => value.ToString("X2")));
            return $"+{offset.Value:0.###} мс · {payload}";
        }
    }
}
