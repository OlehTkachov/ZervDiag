using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class NetworkGapEventAnalyzer
{
    public static IEnumerable<NetworkEvent> Build(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<NetworkPeriodicStreamSnapshot> streams,
        DateTimeOffset captureEnd,
        NetworkProtocolEstimate protocolEstimate)
    {
        foreach (var stream in streams.Where(s => s.IsPeriodic && s.TimeoutMilliseconds.HasValue))
        {
            var ordered = frames
                .Where(f => f.Id == stream.Id && f.IsExtended == stream.IsExtended)
                .OrderBy(f => f.Timestamp)
                .ToArray();
            if (ordered.Length < 4) continue;

            var timeout = stream.TimeoutMilliseconds!.Value;
            var nodeKey = GetNodeKey(stream, protocolEstimate);
            for (var i = 1; i < ordered.Length; i++)
            {
                var gap = (ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalMilliseconds;
                if (gap <= timeout * 1.2) continue;

                yield return Create(
                    stream.IsCanopenHeartbeat ? NetworkEventKind.HeartbeatLost : NetworkEventKind.PeriodicMessageLost,
                    ordered[i - 1].Timestamp.AddMilliseconds(timeout), nodeKey, stream,
                    stream.IsCanopenHeartbeat
                        ? $"Heartbeat пропал; период ≈ {stream.MedianPeriodMilliseconds:0.###} мс."
                        : $"Периодический ID пропал; период ≈ {stream.MedianPeriodMilliseconds:0.###} мс.");
                yield return Create(
                    stream.IsCanopenHeartbeat ? NetworkEventKind.HeartbeatReturned : NetworkEventKind.PeriodicMessageReturned,
                    ordered[i].Timestamp, nodeKey, stream,
                    stream.IsCanopenHeartbeat ? "Heartbeat вернулся." : "Периодический ID вернулся.");
            }

            if (stream.IsConfidentPeriodic &&
                (captureEnd - ordered[^1].Timestamp).TotalMilliseconds > timeout)
            {
                yield return Create(
                    stream.IsCanopenHeartbeat ? NetworkEventKind.HeartbeatLost : NetworkEventKind.NodeDisappeared,
                    ordered[^1].Timestamp.AddMilliseconds(timeout), nodeKey, stream,
                    stream.IsCanopenHeartbeat
                        ? "Heartbeat отсутствует к концу записи."
                        : "Устойчивый периодический поток отсутствует к концу записи.");
            }
        }
    }

    private static NetworkEvent Create(
        NetworkEventKind kind,
        DateTimeOffset timestamp,
        string nodeKey,
        NetworkPeriodicStreamSnapshot stream,
        string description) => new()
    {
        Kind = kind,
        Timestamp = timestamp,
        NodeKey = nodeKey,
        Id = stream.Id,
        IsExtended = stream.IsExtended,
        Description = description
    };

    private static string GetNodeKey(NetworkPeriodicStreamSnapshot stream, NetworkProtocolEstimate protocolEstimate)
    {
        if (stream.IsCanopenHeartbeat && stream.CanopenNodeId.HasValue)
            return $"CANOPEN:{stream.CanopenNodeId.Value:X2}";
        if (stream.SourceAddress.HasValue &&
            protocolEstimate is NetworkProtocolEstimate.J1939Likely or NetworkProtocolEstimate.MixedOrGateway)
            return $"J1939:{stream.SourceAddress.Value:X2}";
        return $"CAN:{(stream.IsExtended ? stream.Id.ToString("X8") : stream.Id.ToString("X3"))}";
    }
}
