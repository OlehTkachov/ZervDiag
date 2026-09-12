using System.Windows;
using CraneCAN.Core.Models;
using CraneCAN.Core.Network;

namespace CraneCAN.App;

public partial class MainWindow
{
    private void AnalyzeNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var liveFrames = _liveReceiver?.Buffer.Snapshot() ?? Array.Empty<CanFrame>();
            var useLive = liveFrames.Count > 0;
            var frames = useLive ? liveFrames : _loadedFrames;
            if (frames.Count == 0)
            {
                MessageBox.Show(
                    "Нет CAN-кадров для анализа. Откройте TRC или запустите Live/Replay.",
                    "Сеть / узлы",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var source = useLive
                ? _liveReceiver?.RawCapturePath ?? _liveReplayPath ?? "Live/Replay buffer"
                : _loadedTrcPath ?? "opened TRC";
            int? bitrate = _machineProfile.Bitrate;
            if (!IsReplaySource && LiveBitrateCombo.SelectedItem is int liveBitrate)
                bitrate = liveBitrate;

            _networkSnapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, source, bitrate);
            RenderNetworkSnapshot(_networkSnapshot);
            StatusText.Text = "Пассивная карта сети построена. CAN-передача не выполнялась.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(FormatException(exception), "Анализ сети",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenderNetworkSnapshot(CanNetworkSnapshot snapshot)
    {
        if (_networkSummaryText is null || _networkNodesGrid is null ||
            _networkFlowsGrid is null || _networkStreamsGrid is null || _networkEventsGrid is null)
            return;

        _networkSummaryText.Text =
            $"Протокол: {snapshot.ProtocolEstimate} ({snapshot.ProtocolConfidence:P0}); " +
            $"кадров {snapshot.FrameCount:N0} (STD {snapshot.StandardFrameCount:N0}, EXT {snapshot.ExtendedFrameCount:N0}); " +
            $"узлов {snapshot.Nodes.Count:N0}; потоков {snapshot.Flows.Count:N0}; событий {snapshot.Events.Count:N0}. " +
            "Это логическая карта наблюдаемого обмена, а не схема физической разводки CAN.";

        _networkNodesGrid.ItemsSource = snapshot.Nodes.Select(node => new NetworkNodeRow(node)).ToArray();
        _networkFlowsGrid.ItemsSource = snapshot.Flows.Select(flow => new NetworkFlowRow(flow)).ToArray();
        _networkStreamsGrid.ItemsSource = snapshot.PeriodicStreams
            .OrderByDescending(stream => stream.IsConfidentPeriodic)
            .ThenByDescending(stream => stream.IsPeriodic)
            .ThenBy(stream => stream.IsExtended)
            .ThenBy(stream => stream.Id)
            .Select(stream => new NetworkStreamRow(stream))
            .ToArray();
        _networkEventsGrid.ItemsSource = snapshot.Events
            .OrderBy(item => item.Timestamp)
            .Select(item => new NetworkEventRow(item))
            .ToArray();

        if (_networkSaveButton is not null) _networkSaveButton.IsEnabled = true;
        if (_networkProfileButton is not null) _networkProfileButton.IsEnabled = true;
    }
}
