using System.Globalization;
using System.Windows;
using CraneCAN.Core.Analysis;
using Microsoft.Win32;

namespace CraneCAN.App;

public partial class MainWindow
{
    private async void ConfigurationSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedFrames.Count == 0)
        {
            MessageBox.Show(
                "Сначала откройте PCAN-View TRC. Снимок строится только по уже прочитанным Rx-кадрам Classical CAN.",
                "Снимок CAN",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CraneCAN observed snapshot (*.cansnapshot)|*.cansnapshot|JSON (*.json)|*.json",
            FileName = $"CraneCAN_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.cansnapshot"
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true, "Формирование read-only снимка наблюдаемой CAN-шины…");
        try
        {
            var snapshot = await Task.Run(() =>
                ConfigurationSnapshotAnalyzer.Create(_loadedFrames, _machineProfile, _loadedTrcPath));
            await ConfigurationSnapshotCodec.SaveAsync(dialog.FileName, snapshot);

            var observedSignals = snapshot.ProfileSignals.Count(signal => signal.ObservedInTrace);
            StatusText.Text =
                $"Снимок CAN сохранён: {snapshot.CanIds.Count:N0} ID, {snapshot.FrameCount:N0} кадров.";

            MessageBox.Show(
                $"Снимок сохранён:\n{dialog.FileName}\n\n" +
                $"Кадров: {snapshot.FrameCount:N0}\n" +
                $"CAN ID: {snapshot.CanIds.Count:N0}\n" +
                $"Сигналов текущего профиля, чьи ID наблюдались: {observedSignals:N0} из {snapshot.ProfileSignals.Count:N0}\n\n" +
                "Важно: это read-only снимок наблюдаемой шины и профиля. " +
                "Он не считывает параметры ECU, не определяет сервисную конфигурацию и не передаёт CAN.",
                "Снимок CAN",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                FormatException(exception),
                "Ошибка сохранения снимка CAN",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
