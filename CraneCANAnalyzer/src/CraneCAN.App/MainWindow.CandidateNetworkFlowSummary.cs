using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

namespace CraneCAN.App;

public partial class MainWindow
{
    private bool _candidateNetworkFlowSummaryInitialized;
    private TextBlock? _guidedNetworkFlowSummaryText;
    private TextBlock? _liveNetworkFlowSummaryText;

    [ModuleInitializer]
    internal static void InstallCandidateNetworkFlowSummaryInitializer()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(CandidateNetworkFlowSummaryMainWindowLoaded));
    }

    private static void CandidateNetworkFlowSummaryMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeCandidateNetworkFlowSummary();
    }

    private void InitializeCandidateNetworkFlowSummary()
    {
        if (_candidateNetworkFlowSummaryInitialized)
            return;

        _candidateNetworkFlowSummaryInitialized = true;
        _guidedNetworkFlowSummaryText = CreateNetworkFlowSummaryTextBlock();
        _liveNetworkFlowSummaryText = CreateNetworkFlowSummaryTextBlock();

        InsertAfter(GuidedQualityText, _guidedNetworkFlowSummaryText);
        InsertAfter(LiveQualityText, _liveNetworkFlowSummaryText);

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(DataGrid));

        if (descriptor is not null)
        {
            descriptor.AddValueChanged(GuidedCandidatesGrid, (_, _) =>
                Dispatcher.BeginInvoke(new Action(UpdateGuidedNetworkFlowSummary)));
            descriptor.AddValueChanged(LiveCandidatesGrid, (_, _) =>
                Dispatcher.BeginInvoke(new Action(UpdateLiveNetworkFlowSummary)));
        }

        UpdateGuidedNetworkFlowSummary();
        UpdateLiveNetworkFlowSummary();
    }

    private static TextBlock CreateNetworkFlowSummaryTextBlock() => new()
    {
        Margin = new Thickness(0, 5, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(66, 84, 102)),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed
    };

    private static void InsertAfter(FrameworkElement anchor, UIElement element)
    {
        if (anchor.Parent is not StackPanel panel)
            return;

        var index = panel.Children.IndexOf(anchor);
        panel.Children.Insert(index >= 0 ? index + 1 : panel.Children.Count, element);
    }

    private void UpdateGuidedNetworkFlowSummary() =>
        UpdateNetworkFlowSummary(GuidedCandidatesGrid, _guidedNetworkFlowSummaryText);

    private void UpdateLiveNetworkFlowSummary() =>
        UpdateNetworkFlowSummary(LiveCandidatesGrid, _liveNetworkFlowSummaryText);

    private static void UpdateNetworkFlowSummary(DataGrid grid, TextBlock? target)
    {
        if (target is null)
            return;

        var candidates = (grid.ItemsSource as IEnumerable<GuidedCandidateRow>)?
            .Select(row => row.Candidate)
            .ToArray() ?? [];
        var groups = CandidateNetworkFlowAnalyzer.Summarize(candidates);

        if (groups.Count == 0)
        {
            target.Text = string.Empty;
            target.Visibility = Visibility.Collapsed;
            return;
        }

        var visibleGroups = groups.Take(4).Select(FormatNetworkFlowGroup).ToArray();
        var suffix = groups.Count > visibleGroups.Length
            ? $"   |   ещё групп: {groups.Count - visibleGroups.Length}"
            : string.Empty;

        target.Text = "Обмен узлов (J1939, оценка): " +
                      string.Join("   |   ", visibleGroups) + suffix;
        target.Visibility = Visibility.Visible;
    }

    private static string FormatNetworkFlowGroup(CandidateNetworkFlowSummary group)
    {
        var route = group.DestinationAddress.HasValue
            ? $"SA {group.SourceAddressText} → DA {group.DestinationAddressText}"
            : $"SA {group.SourceAddressText} → PDU2";
        var kind = group.Kind == CandidateNetworkFlowKind.DirectedPdu1
            ? "адресный"
            : "широковещательный";
        var reaction = group.FirstReactionMilliseconds.HasValue
            ? $"первая {group.FirstReactionMilliseconds.Value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture)} мс"
            : "первая —";

        return $"{route}: кандидатов {group.CandidateCount}, ID {group.DistinctIdCount}, {reaction}, {kind}";
    }
}
