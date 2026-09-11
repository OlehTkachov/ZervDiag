using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

namespace CraneCAN.App;

public partial class MainWindow
{
    private bool _candidateNetworkFlowSummaryInitialized;
    private StackPanel? _guidedNetworkFlowSummaryPanel;
    private StackPanel? _liveNetworkFlowSummaryPanel;
    private CandidateFilterControls? _guidedCandidateFilterControls;
    private CandidateFilterControls? _liveCandidateFilterControls;

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

        _guidedNetworkFlowSummaryPanel = CreateNetworkFlowSummaryPanel();
        _liveNetworkFlowSummaryPanel = CreateNetworkFlowSummaryPanel();
        InsertAfter(GuidedQualityText, _guidedNetworkFlowSummaryPanel);
        InsertAfter(LiveQualityText, _liveNetworkFlowSummaryPanel);

        _guidedCandidateFilterControls = CreateCandidateFilterControls(GuidedCandidatesGrid);
        _liveCandidateFilterControls = CreateCandidateFilterControls(LiveCandidatesGrid);
        InsertAfter(_guidedNetworkFlowSummaryPanel, _guidedCandidateFilterControls.Root);
        InsertAfter(_liveNetworkFlowSummaryPanel, _liveCandidateFilterControls.Root);

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            ItemsControl.ItemsSourceProperty,
            typeof(DataGrid));

        if (descriptor is not null)
        {
            descriptor.AddValueChanged(GuidedCandidatesGrid, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateGuidedNetworkFlowSummary();
                    ApplyCandidateFilter(_guidedCandidateFilterControls, showErrors: false);
                })));
            descriptor.AddValueChanged(LiveCandidatesGrid, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLiveNetworkFlowSummary();
                    ApplyCandidateFilter(_liveCandidateFilterControls, showErrors: false);
                })));
        }

        UpdateGuidedNetworkFlowSummary();
        UpdateLiveNetworkFlowSummary();
        ApplyCandidateFilter(_guidedCandidateFilterControls, showErrors: false);
        ApplyCandidateFilter(_liveCandidateFilterControls, showErrors: false);
    }

    private static StackPanel CreateNetworkFlowSummaryPanel() => new()
    {
        Margin = new Thickness(0, 5, 0, 0),
        Visibility = Visibility.Collapsed
    };

    private CandidateFilterControls CreateCandidateFilterControls(DataGrid grid)
    {
        var pgn = CreateHexFilterTextBox(70, "PGN, например EF00 или 0xEF00");
        var sa = CreateHexFilterTextBox(55, "SA, например 20 или 0x20");
        var da = CreateHexFilterTextBox(55, "DA, например 21 или 0x21");
        var flow = new ComboBox
        {
            Width = 135,
            Margin = new Thickness(3, 1, 8, 1),
            ItemsSource = new[] { "Все направления", "Адресные PDU1", "PDU2" },
            SelectedIndex = 0
        };
        var discreteOnly = new CheckBox
        {
            Content = "дискретные",
            Margin = new Thickness(4, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        var afterActionOnly = new CheckBox
        {
            Content = "только после действия",
            Margin = new Thickness(4, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Скрыть кандидатов с отрицательной реакцией и без измеренного времени реакции"
        };
        var status = new TextBlock
        {
            Margin = new Thickness(8, 3, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(66, 84, 102))
        };
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "Фильтр кандидатов:",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 6, 2),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(CreateFilterLabel("PGN"));
        panel.Children.Add(pgn);
        panel.Children.Add(CreateFilterLabel("SA"));
        panel.Children.Add(sa);
        panel.Children.Add(CreateFilterLabel("DA"));
        panel.Children.Add(da);
        panel.Children.Add(flow);
        panel.Children.Add(discreteOnly);
        panel.Children.Add(afterActionOnly);

        var root = new Border
        {
            Margin = new Thickness(0, 5, 0, 0),
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(199, 210, 222)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        var rootPanel = new StackPanel();
        rootPanel.Children.Add(panel);
        rootPanel.Children.Add(status);
        root.Child = rootPanel;

        var controls = new CandidateFilterControls(
            root,
            grid,
            pgn,
            sa,
            da,
            flow,
            discreteOnly,
            afterActionOnly,
            status);

        var apply = new Button
        {
            Content = "Применить",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(3, 0, 3, 0)
        };
        apply.Click += (_, _) => ApplyCandidateFilter(controls, showErrors: true);
        panel.Children.Add(apply);

        var reset = new Button
        {
            Content = "Сброс",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(3, 0, 3, 0)
        };
        reset.Click += (_, _) =>
        {
            pgn.Text = string.Empty;
            sa.Text = string.Empty;
            da.Text = string.Empty;
            flow.SelectedIndex = 0;
            discreteOnly.IsChecked = false;
            afterActionOnly.IsChecked = false;
            ApplyCandidateFilter(controls, showErrors: false);
        };
        panel.Children.Add(reset);

        return controls;
    }

    private static TextBox CreateHexFilterTextBox(double width, string toolTip) => new()
    {
        Width = width,
        Margin = new Thickness(2, 1, 7, 1),
        ToolTip = toolTip
    };

    private static TextBlock CreateFilterLabel(string text) => new()
    {
        Text = text + ":",
        Margin = new Thickness(2, 3, 1, 2),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void InsertAfter(FrameworkElement anchor, UIElement element)
    {
        if (anchor.Parent is not StackPanel panel)
            return;

        var index = panel.Children.IndexOf(anchor);
        panel.Children.Insert(index >= 0 ? index + 1 : panel.Children.Count, element);
    }

    private void UpdateGuidedNetworkFlowSummary() =>
        UpdateNetworkFlowSummary(
            GuidedCandidatesGrid,
            _guidedNetworkFlowSummaryPanel,
            _guidedCandidateFilterControls);

    private void UpdateLiveNetworkFlowSummary() =>
        UpdateNetworkFlowSummary(
            LiveCandidatesGrid,
            _liveNetworkFlowSummaryPanel,
            _liveCandidateFilterControls);

    private void UpdateNetworkFlowSummary(
        DataGrid grid,
        StackPanel? target,
        CandidateFilterControls? controls)
    {
        if (target is null)
            return;

        target.Children.Clear();
        var candidates = (grid.ItemsSource as IEnumerable<GuidedCandidateRow>)?
            .Select(row => row.Candidate)
            .ToArray() ?? [];
        var groups = CandidateNetworkFlowAnalyzer.Summarize(candidates);

        if (groups.Count == 0)
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }

        target.Children.Add(new TextBlock
        {
            Text = "Обмен узлов (J1939, оценка) — нажмите направление, чтобы отфильтровать таблицу:",
            Foreground = new SolidColorBrush(Color.FromRgb(66, 84, 102)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var routes = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var group in groups.Take(8))
        {
            var captured = group;
            var button = new Button
            {
                Content = FormatNetworkFlowButton(group),
                ToolTip = FormatNetworkFlowGroup(group),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 1, 5, 1),
                FontSize = 11
            };
            button.Click += (_, _) => ApplyNetworkFlowGroupFilter(controls, captured);
            routes.Children.Add(button);
        }

        if (groups.Count > 8)
        {
            routes.Children.Add(new TextBlock
            {
                Text = $"ещё групп: {groups.Count - 8}",
                Margin = new Thickness(4, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(88, 101, 116))
            });
        }

        target.Children.Add(routes);
        target.Visibility = Visibility.Visible;
    }

    private void ApplyNetworkFlowGroupFilter(
        CandidateFilterControls? controls,
        CandidateNetworkFlowSummary group)
    {
        if (controls is null)
            return;

        controls.PgnTextBox.Text = string.Empty;
        controls.SourceAddressTextBox.Text = group.SourceAddressText;
        controls.DestinationAddressTextBox.Text = group.DestinationAddress.HasValue
            ? group.DestinationAddressText
            : string.Empty;
        controls.FlowCombo.SelectedIndex = group.Kind == CandidateNetworkFlowKind.DirectedPdu1 ? 1 : 2;
        ApplyCandidateFilter(controls, showErrors: false);
    }

    private void ApplyCandidateFilter(CandidateFilterControls? controls, bool showErrors)
    {
        if (controls is null)
            return;

        try
        {
            var criteria = ReadCandidateFilterCriteria(controls);
            var source = controls.Grid.ItemsSource as IEnumerable<GuidedCandidateRow>;
            var total = source?.Count() ?? 0;

            if (controls.Grid.ItemsSource is null)
            {
                controls.StatusText.Text = "Показано: 0";
                return;
            }

            var view = CollectionViewSource.GetDefaultView(controls.Grid.ItemsSource);
            if (!view.CanFilter)
            {
                controls.StatusText.Text = $"Показано: {total}. Фильтрация этого представления недоступна.";
                return;
            }

            view.Filter = criteria.IsEmpty
                ? null
                : item => item is GuidedCandidateRow row && criteria.Matches(row.Candidate);
            view.Refresh();

            var visible = view.Cast<object>().Count();
            controls.StatusText.Text = criteria.IsEmpty
                ? $"Показано: {visible} из {total}."
                : $"Фильтр активен: показано {visible} из {total}.";
        }
        catch (FormatException exception)
        {
            controls.StatusText.Text = exception.Message;
            if (showErrors)
            {
                MessageBox.Show(
                    exception.Message,
                    "Фильтр кандидатов",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private static CandidateFilterCriteria ReadCandidateFilterCriteria(CandidateFilterControls controls)
    {
        var pgn = ParseOptionalHex(controls.PgnTextBox.Text, 0x3FFFF, "PGN");
        var sourceAddress = ParseOptionalHex(controls.SourceAddressTextBox.Text, 0xFF, "SA");
        var destinationAddress = ParseOptionalHex(controls.DestinationAddressTextBox.Text, 0xFF, "DA");

        CandidateNetworkFlowKind? flowKind = controls.FlowCombo.SelectedIndex switch
        {
            1 => CandidateNetworkFlowKind.DirectedPdu1,
            2 => CandidateNetworkFlowKind.BroadcastPdu2,
            _ => null
        };

        if (destinationAddress.HasValue && flowKind == CandidateNetworkFlowKind.BroadcastPdu2)
            throw new FormatException("DA применяется только к адресным J1939 PDU1 сообщениям.");
        if (destinationAddress.HasValue && !flowKind.HasValue)
            flowKind = CandidateNetworkFlowKind.DirectedPdu1;

        return new CandidateFilterCriteria
        {
            Pgn = pgn,
            SourceAddress = sourceAddress.HasValue ? (byte)sourceAddress.Value : null,
            DestinationAddress = destinationAddress.HasValue ? (byte)destinationAddress.Value : null,
            FlowKind = flowKind,
            DiscreteOnly = controls.DiscreteOnlyCheckBox.IsChecked == true,
            AfterActionOnly = controls.AfterActionOnlyCheckBox.IsChecked == true
        };
    }

    private static uint? ParseOptionalHex(string text, uint maximum, string fieldName)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
            return null;
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        if (!uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ||
            value > maximum)
        {
            throw new FormatException(
                $"Поле {fieldName} должно содержать HEX-значение от 0x0 до 0x{maximum:X}.");
        }

        return value;
    }

    private static string FormatNetworkFlowButton(CandidateNetworkFlowSummary group)
    {
        var route = group.DestinationAddress.HasValue
            ? $"SA {group.SourceAddressText} → DA {group.DestinationAddressText}"
            : $"SA {group.SourceAddressText} → PDU2";
        var reaction = group.FirstReactionMilliseconds.HasValue
            ? group.FirstReactionMilliseconds.Value.ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture) + " мс"
            : "—";
        return $"{route} · {group.CandidateCount} канд. · {reaction}";
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
            ? $"первая {group.FirstReactionMilliseconds.Value.ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture)} мс"
            : "первая —";

        return $"{route}: кандидатов {group.CandidateCount}, ID {group.DistinctIdCount}, {reaction}, {kind}";
    }

    private sealed record CandidateFilterControls(
        Border Root,
        DataGrid Grid,
        TextBox PgnTextBox,
        TextBox SourceAddressTextBox,
        TextBox DestinationAddressTextBox,
        ComboBox FlowCombo,
        CheckBox DiscreteOnlyCheckBox,
        CheckBox AfterActionOnlyCheckBox,
        TextBlock StatusText);
}
