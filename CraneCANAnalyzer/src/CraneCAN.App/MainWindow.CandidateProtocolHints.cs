using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

namespace CraneCAN.App;

public partial class MainWindow
{
    private bool _candidateProtocolHintsInitialized;

    [ModuleInitializer]
    internal static void InstallCandidateProtocolHintInitializer()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(CandidateProtocolHintsMainWindowLoaded));
    }

    private static void CandidateProtocolHintsMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeCandidateProtocolHintColumns();
    }

    private void InitializeCandidateProtocolHintColumns()
    {
        if (_candidateProtocolHintsInitialized)
            return;

        _candidateProtocolHintsInitialized = true;
        AddCandidateProtocolHintColumns(GuidedCandidatesGrid);
        AddCandidateProtocolHintColumns(LiveCandidatesGrid);
    }

    private static void AddCandidateProtocolHintColumns(DataGrid grid)
    {
        if (grid.Columns.Any(column => string.Equals(
                column.Header?.ToString(),
                "PGN",
                StringComparison.Ordinal)))
            return;

        var idIndex = -1;
        for (var index = 0; index < grid.Columns.Count; index++)
        {
            if (string.Equals(
                    grid.Columns[index].Header?.ToString(),
                    "ID",
                    StringComparison.Ordinal))
            {
                idIndex = index;
                break;
            }
        }

        var insertIndex = idIndex >= 0 ? idIndex + 1 : grid.Columns.Count;

        grid.Columns.Insert(insertIndex++, new DataGridTextColumn
        {
            Header = "PGN",
            Width = 82,
            Binding = new Binding(nameof(GuidedCandidateRow.Candidate))
            {
                Converter = CandidatePgnConverter.Instance
            }
        });

        grid.Columns.Insert(insertIndex++, new DataGridTextColumn
        {
            Header = "SA",
            Width = 62,
            Binding = new Binding(nameof(GuidedCandidateRow.Candidate))
            {
                Converter = CandidateSourceAddressConverter.Instance
            }
        });

        grid.Columns.Insert(insertIndex, new DataGridTextColumn
        {
            Header = "Тип (оценка)",
            Width = 125,
            Binding = new Binding(nameof(GuidedCandidateRow.Candidate))
            {
                Converter = CandidateTypeHintConverter.Instance
            }
        });
    }

    private sealed class CandidatePgnConverter : IValueConverter
    {
        public static CandidatePgnConverter Instance { get; } = new();

        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture) =>
            value is GuidedCandidate candidate &&
            CandidateProtocolHints.TryDecodeJ1939(
                candidate.Id,
                candidate.IsExtended,
                out var info)
                ? info.PgnText
                : "—";

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class CandidateSourceAddressConverter : IValueConverter
    {
        public static CandidateSourceAddressConverter Instance { get; } = new();

        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture) =>
            value is GuidedCandidate candidate &&
            CandidateProtocolHints.TryDecodeJ1939(
                candidate.Id,
                candidate.IsExtended,
                out var info)
                ? info.SourceAddressText
                : "—";

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture) => Binding.DoNothing;
    }

    private sealed class CandidateTypeHintConverter : IValueConverter
    {
        public static CandidateTypeHintConverter Instance { get; } = new();

        public object Convert(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture) =>
            value is GuidedCandidate candidate
                ? CandidateProtocolHints.Classify(candidate)
                : "—";

        public object ConvertBack(
            object value,
            Type targetType,
            object parameter,
            CultureInfo culture) => Binding.DoNothing;
    }
}
