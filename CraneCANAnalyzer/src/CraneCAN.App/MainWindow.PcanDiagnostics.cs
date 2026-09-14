using System.Windows;
using CraneCAN.Core.Drivers;
using CraneCAN.Driver.PcanBasic;

namespace CraneCAN.App;

public partial class MainWindow
{
    private bool _pcanDiagnosticBusy;

    private async void DiagnosePcanLiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pcanDiagnosticBusy)
            return;

        if (_liveConnectionReady)
        {
            MessageBox.Show(
                "Live CAN уже подключён. Сначала нажмите «ОТКЛЮЧИТЬ» во вкладке LIVE GUIDED, затем повторите аппаратную проверку.",
                "Проверка PCAN / Live CAN",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _pcanDiagnosticBusy = true;
        if (sender is FrameworkElement element)
            element.IsEnabled = false;

        try
        {
            StatusText.Text = "PCAN: поиск USB-каналов…";
            await using var discovery = new PcanBasicCanDriver();
            var channels = await discovery.DiscoverChannelsAsync();
            if (channels.Count == 0)
            {
                ShowPcanDiagnosticResult(
                    "PCAN-USB не обнаружен. Проверьте USB, PEAK Device Driver x64 и наличие PCANBasic.dll.",
                    MessageBoxImage.Warning);
                return;
            }

            // Select the first physical PCAN-USB channel for the field diagnostic. The normal LIVE GUIDED
            // controls remain available if the operator needs another channel afterwards.
            var channel = channels[0];
            LiveSourceCombo.SelectedIndex = 1;
            LiveChannelCombo.ItemsSource = channels;
            LiveChannelCombo.SelectedItem = channel;
            if (LiveChannelCombo.SelectedItem is null)
                LiveChannelCombo.SelectedIndex = 0;

            LiveBitrateCombo.ItemsSource = PcanLiveProbe.CommonBitrates;
            LiveBitrateCombo.SelectedItem = 250_000;

            StatusText.Text = $"PCAN найден: {channel.DisplayName}. Пассивно ищу bitrate…";
            LiveInstructionText.Text = "Проверяю реальный CAN только в LISTEN ONLY. Передача CAN отсутствует.";

            var report = await PcanLiveProbe.ProbeAsync(
                channel.Id,
                PcanLiveProbe.CommonBitrates,
                TimeSpan.FromMilliseconds(450));

            if (report.DetectedBitrate is not int bitrate || report.BestAttempt is not { } best)
            {
                var detail = report.Describe();
                LiveConnectionText.Text = "NO CAN FRAMES";
                LiveInstructionText.Text =
                    "PCAN-USB найден, но реальные CAN-кадры не получены. Проверьте CAN_H/CAN_L/GND, питание сети и фактический bitrate.";
                ShowPcanDiagnosticResult(
                    $"Адаптер найден: {channel.DisplayName}\n\n{detail}\n\n" +
                    "CraneCAN проверял канал только пассивно (LISTEN ONLY). Если PCAN-View на этом же подключении видит кадры, сообщите его bitrate и пришлите скрин PCAN-View — тогда проблема находится в Live-драйвере CraneCAN.",
                    MessageBoxImage.Warning);
                return;
            }

            LiveBitrateCombo.SelectedItem = bitrate;
            LiveConnectionText.Text = "CAN FOUND";
            LiveInstructionText.Text =
                $"Реальный CAN найден: {bitrate:N0} bit/s, принято {best.Frames:N0} тестовых кадров. Подключаю постоянный Live-приём…";
            StatusText.Text = $"PCAN: CAN найден на {bitrate:N0} bit/s; тестовых кадров {best.Frames:N0}.";

            // Reuse the normal Live connection path after the passive probe. It opens the same channel
            // in hardware LISTEN ONLY and starts the continuous receiver/raw capture.
            MainTabs.SelectedIndex = 0;
            LiveConnectButton_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception)
        {
            ShowPcanDiagnosticResult(FormatException(exception), MessageBoxImage.Error);
        }
        finally
        {
            _pcanDiagnosticBusy = false;
            if (sender is FrameworkElement element)
                element.IsEnabled = true;
        }
    }

    private void ShowPcanDiagnosticResult(string message, MessageBoxImage image)
    {
        StatusText.Text = message.Replace('\n', ' ');
        MessageBox.Show(message, "Проверка PCAN / Live CAN", MessageBoxButton.OK, image);
    }
}
