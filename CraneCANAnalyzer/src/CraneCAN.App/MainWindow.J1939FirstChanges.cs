using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Storage;

namespace CraneCAN.App;

public partial class MainWindow
{
    private void ShowJ1939FirstChangesWindow(
        LoadedIncidentPackage package,
        J1939FirstChangesResult result,
        Window owner)
    {
        var earliest = result.Earliest is null
            ? "PGN-normalized кандидатов не найдено."
            : $"Первый PGN-кандидат: {FormatReaction(result.Earliest.ReactionMilliseconds)} · PGN {result.Earliest.PgnText}.";

        var sourceChanges = result.SourceAddressChanges.Count == 0
            ? "Изменения Source Address между BASELINE/SEARCH: нет."
            : "Source Address изменился без автоматического PGN lifecycle event:\n• " +
              string.Join(
                  "\n• ",
                  result.SourceAddressChanges.Select(change =>
                      $"PGN {change.PgnText}" +
                      (change.DestinationAddress.HasValue
                          ? $" · DA 0x{change.DestinationAddress.Value:X2}"
                          : string.Empty) +
                      $" · SA {FormatAddresses(change.BaselineSourceAddresses)} → {FormatAddresses(change.SearchSourceAddresses)}"));

        var warnings = result.Warnings.Count == 0
            ? "Предупреждения: нет."
            : "Предупреждения:\n• " +
              string.Join("\n• ", result.Warnings);

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Incident {package.Incident.IncidentId:N}\n" +
                "J1939 PGN-normalized First Changes — параллельный read-only анализ; raw exact-ID анализ не изменён.\n" +
                $"29-bit Rx frames: BASELINE {result.ExtendedBaselineFrameCount:N0}; SEARCH {result.ExtendedSearchFrameCount:N0}.\n" +
                $"Кандидаты: HIGH {result.HighCount}; MEDIUM {result.MediumCount}; INFO {result.InfoCount}. {earliest}\n" +
                $"Подавлено exact-ID lifecycle кандидатов: {result.SuppressedExactIdLifecycleCandidates:N0}.\n" +
                sourceChanges + "\n" +
                warnings + "\n" +
                "Identity в этом окне: PGN + PDU1 Destination Address. Priority и Source Address исключены. " +
                "Это J1939-интерпретация наблюдаемого CAN, а не доказательство причины неисправности. CAN Tx отсутствует."
        };

        var rows = result.Candidates
            .Select(candidate =>
                new J1939FirstChangeRow(candidate))
            .ToArray();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            EnableRowVirtualization = true,
            GridLinesVisibility =
                DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            ItemsSource = rows
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Время",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.ReactionText)),
            Width = 90
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Приоритет",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.PriorityText)),
            Width = 85
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "PGN",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.PgnText)),
            Width = 90
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "DA",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.DestinationText)),
            Width = 65
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Место",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.LocationText)),
            Width = 90
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Тип",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.KindText)),
            Width = 145
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Baseline",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.BaselineText)),
            Width = 150
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Наблюдалось",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.ObservedText)),
            Width = 170
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "SA baseline",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.BaselineSourcesText)),
            Width = 120
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "SA search",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.SearchSourcesText)),
            Width = 120
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Raw ID baseline",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.BaselineRawIdsText)),
            Width = 180
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Raw ID search",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.SearchRawIdsText)),
            Width = 180
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Согласие",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.AgreementText)),
            Width = 90
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Повтор",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.ConfirmationText)),
            Width = 75
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Интерпретация",
            Binding = new Binding(
                nameof(J1939FirstChangeRow.Description)),
            Width = new DataGridLength(
                1,
                DataGridLengthUnitType.Star)
        });

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
        buttons.Children.Add(close);

        var layout = new Grid
        {
            Margin = new Thickness(12)
        };
        layout.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });
        layout.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(
                    1,
                    GridUnitType.Star)
            });
        layout.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });

        Grid.SetRow(summary, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(summary);
        layout.Children.Add(grid);
        layout.Children.Add(buttons);

        var window = new Window
        {
            Owner = owner,
            Title =
                "CraneCAN — J1939 PGN First Changes",
            Width = 1580,
            Height = 720,
            MinWidth = 1080,
            MinHeight = 500,
            WindowStartupLocation =
                WindowStartupLocation.CenterOwner,
            Content = layout
        };

        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private sealed record J1939FirstChangeRow(
        J1939FirstChangeCandidate Candidate)
    {
        public string ReactionText =>
            FormatReaction(
                Candidate.ReactionMilliseconds);

        public string PriorityText =>
            Candidate.Priority switch
            {
                IncidentTransitionPriority.High =>
                    "HIGH",
                IncidentTransitionPriority.Medium =>
                    "MEDIUM",
                _ => "INFO"
            };

        public string PgnText =>
            Candidate.PgnText;

        public string DestinationText =>
            Candidate.DestinationAddress.HasValue
                ? $"0x{Candidate.DestinationAddress.Value:X2}"
                : "—";

        public string LocationText =>
            Candidate.DataIndex.HasValue
                ? $"DATA[{Candidate.DataIndex.Value}]"
                : "PGN/DLC";

        public string KindText =>
            Candidate.Kind switch
            {
                IncidentTransitionKind.IdAppeared =>
                    "PGN появился",
                IncidentTransitionKind.PeriodicIdStopped =>
                    "PGN прекратился",
                IncidentTransitionKind.DlcChanged =>
                    "DLC изменился",
                IncidentTransitionKind.ByteChanged =>
                    "байт изменился",
                _ => Candidate.Kind.ToString()
            };

        public string BaselineText =>
            Candidate.BaselineValue;

        public string ObservedText =>
            Candidate.ObservedValue;

        public string BaselineSourcesText =>
            FormatAddresses(
                Candidate.BaselineSourceAddresses);

        public string SearchSourcesText =>
            FormatAddresses(
                Candidate.SearchSourceAddresses);

        public string BaselineRawIdsText =>
            FormatRawIds(
                Candidate.BaselineRawIds);

        public string SearchRawIdsText =>
            FormatRawIds(
                Candidate.SearchRawIds);

        public string AgreementText =>
            Candidate.BaselineAgreementPercent
                .ToString(
                    "0.#",
                    CultureInfo.InvariantCulture) +
            "%";

        public string ConfirmationText =>
            Candidate.ConfirmationCount.ToString(
                CultureInfo.InvariantCulture);

        public string Description =>
            Candidate.Description;
    }

    private static string FormatAddresses(
        IReadOnlyList<int> addresses) =>
        addresses.Count == 0
            ? "—"
            : string.Join(
                ", ",
                addresses.Select(address =>
                    $"0x{address:X2}"));

    private static string FormatRawIds(
        IReadOnlyList<uint> ids) =>
        ids.Count == 0
            ? "—"
            : string.Join(
                ", ",
                ids.Select(id =>
                    id.ToString(
                        "X8",
                        CultureInfo.InvariantCulture)));
}
