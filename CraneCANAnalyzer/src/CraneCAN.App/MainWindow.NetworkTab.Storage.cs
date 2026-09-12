using System.Windows;
using CraneCAN.Core.Storage;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async void SaveNetworkSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_networkSnapshot is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить пассивный снимок сети CraneCAN",
            Filter = "Сетевой снимок CraneCAN (*.cannetwork)|*.cannetwork",
            DefaultExt = ".cannetwork",
            AddExtension = true,
            FileName = "network.cannetwork"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await CanNetworkSnapshotCodec.SaveAsync(dialog.FileName, _networkSnapshot);
            StatusText.Text = $"Сетевой снимок сохранён: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Сохранение сетевого снимка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OpenNetworkSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть пассивный снимок сети CraneCAN",
            Filter = "Сетевой снимок CraneCAN (*.cannetwork)|*.cannetwork",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _networkSnapshot = await CanNetworkSnapshotCodec.LoadAsync(dialog.FileName);
            RenderNetworkSnapshot(_networkSnapshot);
            StatusText.Text = $"Открыт сетевой снимок: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Открытие сетевого снимка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
