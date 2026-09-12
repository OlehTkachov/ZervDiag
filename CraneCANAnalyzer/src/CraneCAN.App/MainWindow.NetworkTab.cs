using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CraneCAN.Core.Network;

namespace CraneCAN.App;

public partial class MainWindow
{
    private bool _networkTabInitialized;
    private TabItem? _networkTab;
    private TextBlock? _networkSummaryText;
    private DataGrid? _networkNodesGrid;
    private DataGrid? _networkFlowsGrid;
    private DataGrid? _networkStreamsGrid;
    private DataGrid? _networkEventsGrid;
    private Button? _networkSaveButton;
    private Button? _networkProfileButton;
    private CanNetworkSnapshot? _networkSnapshot;

    [ModuleInitializer]
    internal static void InstallNetworkDiscoveryTabInitializer()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(NetworkDiscoveryMainWindowLoaded));
    }

    private static void NetworkDiscoveryMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
            window.InitializeNetworkDiscoveryTab();
    }

    private void InitializeNetworkDiscoveryTab()
    {
        if (_networkTabInitialized)
            return;

        _networkTabInitialized = true;
        _networkTab = new TabItem { Header = "Сеть / узлы" };

        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = BuildNetworkToolbar();
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        _networkSummaryText = new TextBlock
        {
            Text = "Пассивный анализ сети ещё не выполнен. Откройте TRC или запустите Live/Replay и нажмите «Анализировать сеть». Передача CAN отсутствует.",
            Margin = new Thickness(4, 8, 4, 8),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_networkSummaryText, 1);
        root.Children.Add(_networkSummaryText);

        var detailsTabs = BuildNetworkDetailsTabs();
        Grid.SetRow(detailsTabs, 2);
        root.Children.Add(detailsTabs);

        _networkTab.Content = root;
        MainTabs.Items.Add(_networkTab);
    }

    private WrapPanel BuildNetworkToolbar()
    {
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

        var analyze = NetworkButton("Анализировать сеть", "Построить логическую карту только по уже принятым кадрам CAN.");
        analyze.Click += AnalyzeNetworkButton_Click;
        toolbar.Children.Add(analyze);

        _networkSaveButton = NetworkButton("Сохранить .cannetwork…", "Сохранить пассивный снимок сети.");
        _networkSaveButton.IsEnabled = false;
        _networkSaveButton.Click += SaveNetworkSnapshotButton_Click;
        toolbar.Children.Add(_networkSaveButton);

        var open = NetworkButton("Открыть .cannetwork…", "Открыть ранее сохранённый снимок сети.");
        open.Click += OpenNetworkSnapshotButton_Click;
        toolbar.Children.Add(open);

        var compare = NetworkButton("GOOD / FAULT…", "Сравнить два сетевых снимка без передачи CAN.");
        compare.Click += CompareNetworkSnapshotsButton_Click;
        toolbar.Children.Add(compare);

        _networkProfileButton = NetworkButton("Добавить сеть в профиль", "Сохранить автоматически наблюдённые узлы и потоки в Machine Profile, не перезаписывая документированные данные.");
        _networkProfileButton.IsEnabled = false;
        _networkProfileButton.Click += SaveNetworkToProfileButton_Click;
        toolbar.Children.Add(_networkProfileButton);

        return toolbar;
    }

    private static Button NetworkButton(string text, string toolTip) => new()
    {
        Content = text,
        ToolTip = toolTip,
        Padding = new Thickness(12, 5, 12, 5),
        Margin = new Thickness(2)
    };
}
