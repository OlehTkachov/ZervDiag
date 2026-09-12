namespace CraneCAN.Core.Network;

internal static partial class CanopenNodeAnalyzer
{
    private static NetworkNodeSnapshot CreateNode(
        CanopenObservedNode node,
        DateTimeOffset captureStart,
        DateTimeOffset captureEnd,
        IReadOnlyList<NetworkPeriodicStreamSnapshot> streams,
        NetworkProtocolEstimateResult protocol,
        ICollection<NetworkEvent> events)
    {
        var c = CanopenNodeContextBuilder.Build(node, captureStart, captureEnd, streams);
        CanopenNodeEvents.Add(node, events);
        var span = node.Frames[^1].Frame.Timestamp - node.Frames[0].Frame.Timestamp;
        var evidence = new List<string>();
        if (c.Heartbeat is not null) evidence.Add("Heartbeat");
        if (c.Bootups.Length > 0) evidence.Add("Boot-up");
        if (c.Emcy) evidence.Add("EMCY");
        if (c.CobIds.Length >= 2) evidence.Add($"COB-ID {c.CobIds.Length}");

        return new NetworkNodeSnapshot
        {
            NodeKey = $"CANOPEN:{node.NodeId:X2}",
            Protocol = NetworkNodeProtocol.Canopen,
            Address = node.NodeId,
            AddressText = $"Node {node.NodeId}",
            Identity = "CANopen Node-ID",
            State = c.LastHeartbeat?.Info.NmtStateText ?? c.Health.ToString().ToUpperInvariant(),
            FirstSeen = node.Frames[0].Frame.Timestamp,
            LastSeen = node.Frames[^1].Frame.Timestamp,
            FrameCount = node.Frames.Count,
            AverageFrequencyHertz = span.TotalSeconds > 0 ? node.Frames.Count / span.TotalSeconds : 0,
            PeriodicityQuality = c.Heartbeat?.JitterPercent is double jitter ? Math.Clamp(100 - jitter, 0, 100) : 0,
            PgnOrCobIds = c.CobIds.Select(id => $"0x{id:X3}").OrderBy(x => x).ToList(),
            DirectedPeers = [],
            Health = c.Health,
            Confidence = c.Heartbeat is not null ? Math.Max(0.8, protocol.Confidence) : 0.45,
            Evidence = evidence,
            CanopenNmtState = c.LastHeartbeat?.Info.NmtStateText,
            HeartbeatPeriodMilliseconds = c.Heartbeat?.MedianPeriodMilliseconds,
            BootUpObserved = c.Bootups.Length > 0,
            EmcyObserved = c.Emcy
        };
    }
}
