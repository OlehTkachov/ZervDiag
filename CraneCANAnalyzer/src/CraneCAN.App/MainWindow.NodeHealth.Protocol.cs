using CraneCAN.Core.Live;
using CraneCAN.Core.Network;

namespace CraneCAN.App;

public partial class MainWindow
{
    private static string DescribeNodeHealthProtocolContext(
        LiveCanReceiver receiver,
        NodeHealthEvent healthEvent)
    {
        var frames = receiver.Buffer.Snapshot();

        if (!healthEvent.IsExtended && healthEvent.Id is >= 0x701 and <= 0x77F)
        {
            var heartbeatEvidence = frames
                .Where(frame => !frame.IsExtended && frame.Id == healthEvent.Id && frame.Data.Length == 1)
                .TakeLast(12)
                .Count(frame => frame.Data[0] is 0x00 or 0x04 or 0x05 or 0x7F);

            if (heartbeatEvidence >= 3)
            {
                var nodeId = healthEvent.Id - 0x700;
                return $" CANopen Heartbeat Node {nodeId}; семантика подтверждена повторяющимися допустимыми NMT-state кадрами.";
            }
        }

        if (healthEvent.IsExtended &&
            J1939PassiveParser.TryParse(healthEvent.Id, true, out var info))
        {
            var snapshot = PassiveNetworkDiscoveryAnalyzer.Analyze(frames, "live-node-health");
            if (snapshot.ProtocolEstimate is NetworkProtocolEstimate.J1939Likely or NetworkProtocolEstimate.MixedOrGateway)
            {
                var destination = info.DestinationAddress.HasValue
                    ? $" -> DA 0x{info.DestinationAddress.Value:X2}"
                    : " -> PDU2";
                return $" J1939: SA 0x{info.SourceAddress:X2}{destination}, PGN 0x{info.Pgn:X}.";
            }
        }

        return string.Empty;
    }
}
