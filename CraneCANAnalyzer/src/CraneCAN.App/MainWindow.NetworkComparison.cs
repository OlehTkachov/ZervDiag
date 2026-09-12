using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CraneCAN.Core.Network;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async void CompareNetworkSnapshotsButton_Click(object sender, RoutedEventArgs e)
    {
        var goodDialog = new OpenFileDialog
        {
            Title = "GOOD — выберите исправный сетевой снимок",
            Filter = "Сетевой снимок CraneCAN (*.cannetwork)|*.cannetwork",
            CheckFileExists = true
        };
        if (goodDialog.ShowDialog(this) != true) return;

        var faultDialog = new OpenFileDialog
        {
            Title = "FAULT — выберите неисправный сетевой снимок",
            Filter = "Сетевой снимок CraneCAN (*.cannetwork)|*.cannetwork",
            CheckFileExists = true
        };
        if (faultDialog.ShowDialog(this) != true) return;

        try
        {
            var good = await CanNetworkSnapshotCodec.LoadAsync(goodDialog.FileName);
            var fault = await CanNetworkSnapshotCodec.LoadAsync(faultDialog.FileName);
            var comparison = NetworkSnapshotComparer.Compare(good, fault);
            ShowNetworkComparisonWindow(comparison, goodDialog.FileName, faultDialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "GOOD/FAULT сети",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowNetworkComparisonWindow(
        NetworkSnapshotComparisonResult comparison,
        string goodPath,
        string faultPath)
    {
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var summary = new TextBlock
        {
            Text = $"GOOD: {System.IO.Path.GetFileName(goodPath)}\nFAULT: {System.IO.Path.GetFileName(faultPath)}\nРазличий: {comparison.Count}. Это наблюдаемое различие сетевых снимков, а не автоматическое доказательство неисправного ECU.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        layout.Children.Add(summary);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            ItemsSource = comparison.Differences,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Тип", Binding = new Binding(nameof(NetworkSnapshotDifference.Kind)), Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ключ", Binding = new Binding(nameof(NetworkSnapshotDifference.Key)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "GOOD", Binding = new Binding(nameof(NetworkSnapshotDifference.GoodValue)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "FAULT", Binding = new Binding(nameof(NetworkSnapshotDifference.FaultValue)), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Описание", Binding = new Binding(nameof(NetworkSnapshotDifference.Description)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Grid.SetRow(grid, 1);
        layout.Children.Add(grid);

        var close = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4)
        };
        Grid.SetRow(close, 2);
        layout.Children.Add(close);

        var window = new Window
        {
            Owner = this,
            Title = "CraneCAN — GOOD/FAULT карта сети",
            Width = 1100,
            Height = 650,
            MinWidth = 850,
            MinHeight = 450,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }
}
