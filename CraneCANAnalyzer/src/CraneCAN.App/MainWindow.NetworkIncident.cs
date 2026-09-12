using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Network;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async void AnalyzeIncidentNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите сохранённый CraneCAN incident",
            Filter = "CraneCAN incident (*.canincident)|*.canincident",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var package = await PreFaultIncidentCodec.LoadAsync(dialog.FileName);
            var result = IncidentNetworkAnalyzer.Analyze(package.Incident, package.Source.Bitrate);
            ShowIncidentNetworkWindow(result, dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Сеть incident",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowIncidentNetworkWindow(IncidentNetworkAnalysisResult result, string path)
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summary = new TextBlock
        {
            Text = $"{System.IO.Path.GetFileName(path)} | BEFORE: {result.Before.Nodes.Count} узлов, AFTER: {result.After.Nodes.Count} узлов, различий: {result.Changes.Count}. " +
                   "Анализ пассивный; различие не является автоматическим доказательством неисправного ECU.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(summary);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "До / после", Content = BuildIncidentNetworkDifferencesGrid(result) });
        tabs.Items.Add(new TabItem { Header = "Сетевые события", Content = BuildIncidentNetworkTimelineGrid(result) });
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        var close = new Button { Content = "Закрыть", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(close, 2);
        root.Children.Add(close);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — сеть вокруг incident marker",
            Width = 1200,
            Height = 680,
            MinWidth = 900,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private static DataGrid BuildIncidentNetworkDifferencesGrid(IncidentNetworkAnalysisResult result)
    {
        var grid = CreateNetworkGrid();
        grid.ItemsSource = result.Changes.Differences;
        grid.Columns.Add(new DataGridTextColumn { Header = "Тип", Binding = new Binding(nameof(NetworkSnapshotDifference.Kind)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ключ", Binding = new Binding(nameof(NetworkSnapshotDifference.Key)), Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = "BEFORE", Binding = new Binding(nameof(NetworkSnapshotDifference.GoodValue)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "AFTER", Binding = new Binding(nameof(NetworkSnapshotDifference.FaultValue)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Описание", Binding = new Binding(nameof(NetworkSnapshotDifference.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        return grid;
    }

    private static DataGrid BuildIncidentNetworkTimelineGrid(IncidentNetworkAnalysisResult result)
    {
        var grid = CreateNetworkGrid();
        var rows = result.Timeline.Select(item => new
        {
            Relative = item.RelativeMilliseconds >= 0 ? $"+{item.RelativeMilliseconds:0.###} ms" : $"{item.RelativeMilliseconds:0.###} ms",
            item.Kind,
            Node = item.NodeKey,
            Id = item.Id.HasValue ? item.Id.Value.ToString("X8") : "-",
            item.Description
        }).ToArray();
        grid.ItemsSource = rows;
        grid.AutoGenerateColumns = true;
        return grid;
    }
}
