using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;

namespace CraneCAN.App;

public partial class MainWindow
{
    [ModuleInitializer]
    internal static void RegisterJ1939TraceUi()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(J1939TraceMainWindowLoaded));
    }

    private static void J1939TraceMainWindowLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MainWindow window ||
            window.MachineNameTextBox.Parent is not Grid profileGrid)
        {
            return;
        }

        var buttonPanel = profileGrid.Children
            .OfType<WrapPanel>()
            .FirstOrDefault();
        if (buttonPanel is null ||
            buttonPanel.Children
                .OfType<Button>()
                .Any(button => string.Equals(
                    button.Tag as string,
                    "j1939-trace-analysis",
                    StringComparison.Ordinal)))
        {
            return;
        }

        var button = new Button
        {
            Content = "J1939 TRC…",
            Tag = "j1939-trace-analysis",
            ToolTip =
                "Декодировать уже открытый TRC по J1939 PGN/SPN сигналам текущего Machine Profile. Source Address может меняться; CAN Tx отсутствует."
        };
        button.Click += window.J1939TraceButton_Click;
        buttonPanel.Children.Add(button);
    }

    private void J1939TraceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_loadedFrames.Count == 0)
        {
            MessageBox.Show(
                "Сначала откройте TRC. Анализ выполняется только по уже сохранённым/полученным Rx кадрам и ничего не передаёт в CAN.",
                "J1939 TRC",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        UpdateProfileFromFields();
        var result = J1939TraceSignalAnalyzer.Analyze(
            _loadedFrames,
            _machineProfile);

        var rows = result.Signals
            .Select(summary => new J1939TraceRow(summary))
            .ToArray();
        if (rows.Length == 0)
        {
            MessageBox.Show(
                string.Join(
                    Environment.NewLine,
                    result.Warnings.DefaultIfEmpty(
                        "В Machine Profile нет J1939 PGN/SPN сигналов.")),
                "J1939 TRC",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var warnings = result.Warnings.Count == 0
            ? "Предупреждения: нет."
            : "Предупреждения:\n• " + string.Join("\n• ", result.Warnings);
        var summaryText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                $"Extended Rx кадров в трассе: {result.ExtendedRxFrameCount:N0}; " +
                $"кадров, совпавших хотя бы с одним J1939 signal: {result.FramesMatchingProfile:N0}; " +
                $"наблюдавшихся Profile signals: {result.ObservedSignalCount:N0}/{result.Signals.Count:N0}.\n" +
                warnings + "\n" +
                "Сопоставление выполняется по PGN. Source Address может изменяться; для PDU1 Destination Address остаётся фиксированным. " +
                "Engineering values вычислены по Machine Profile / DBC evidence. CAN Tx отсутствует."
        };

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
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "PGN",
            Binding = new Binding(nameof(J1939TraceRow.PgnText)),
            Width = 95
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "SPN",
            Binding = new Binding(nameof(J1939TraceRow.SpnText)),
            Width = 70
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Signal",
            Binding = new Binding(nameof(J1939TraceRow.SignalName)),
            Width = 190
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Status",
            Binding = new Binding(nameof(J1939TraceRow.StatusText)),
            Width = 90
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Source Address",
            Binding = new Binding(nameof(J1939TraceRow.SourceAddressesText)),
            Width = 150
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Frames",
            Binding = new Binding(nameof(J1939TraceRow.FramesText)),
            Width = 110
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Min … Max",
            Binding = new Binding(nameof(J1939TraceRow.RangeText)),
            Width = 170
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Последнее",
            Binding = new Binding(nameof(J1939TraceRow.LatestText)),
            Width = 140
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Первый / последний",
            Binding = new Binding(nameof(J1939TraceRow.TimeRangeText)),
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
        Grid.SetRow(summaryText, 0);
        Grid.SetRow(grid, 1);
        Grid.SetRow(close, 2);
        layout.Children.Add(summaryText);
        layout.Children.Add(grid);
        layout.Children.Add(close);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — J1939 PGN/SPN из TRC",
            Width = 1160,
            Height = 650,
            MinWidth = 820,
            MinHeight = 450,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private sealed record J1939TraceRow(J1939TraceSignalSummary Summary)
    {
        public string PgnText => $"0x{Summary.Pgn:X5}";
        public string SpnText => Summary.Spn?.ToString(CultureInfo.InvariantCulture) ?? "—";
        public string SignalName => string.IsNullOrWhiteSpace(Summary.SignalName)
            ? "(без имени)"
            : Summary.SignalName;
        public string StatusText => Summary.Confidence.ToString().ToUpperInvariant();
        public string SourceAddressesText => Summary.SourceAddresses.Count == 0
            ? "—"
            : string.Join(", ", Summary.SourceAddresses.Select(value => $"0x{value:X2}"));
        public string FramesText =>
            $"{Summary.DecodedFrameCount:N0}/{Summary.MatchingFrameCount:N0}" +
            (Summary.ShortFrameCount > 0
                ? $"; short {Summary.ShortFrameCount:N0}"
                : string.Empty);
        public string RangeText =>
            Summary.MinimumEngineeringValue.HasValue && Summary.MaximumEngineeringValue.HasValue
                ? $"{FormatJ1939Number(Summary.MinimumEngineeringValue.Value)} … {FormatJ1939Number(Summary.MaximumEngineeringValue.Value)}{UnitSuffix}"
                : "—";
        public string LatestText => Summary.LatestEngineeringValue.HasValue
            ? $"{FormatJ1939Number(Summary.LatestEngineeringValue.Value)}{UnitSuffix}"
            : "—";
        public string TimeRangeText =>
            Summary.FirstTimestamp.HasValue && Summary.LastTimestamp.HasValue
                ? $"{Summary.FirstTimestamp.Value:O} / {Summary.LastTimestamp.Value:O}"
                : "—";
        private string UnitSuffix => string.IsNullOrWhiteSpace(Summary.Unit)
            ? string.Empty
            : " " + Summary.Unit.Trim();
    }

    private static string FormatJ1939Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
