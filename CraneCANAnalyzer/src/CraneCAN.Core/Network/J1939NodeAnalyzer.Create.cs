namespace CraneCAN.Core.Network;

internal static partial class J1939NodeAnalyzer
{
    private static NetworkNodeSnapshot CreateNode(
        J1939ObservedNode node,
        DateTimeOffset captureEnd,
        IReadOnlyList<NetworkPeriodicStreamSnapshot> streams,
        NetworkProtocolEstimateResult protocol,
        ICollection<NetworkEvent> events)
    {
        var c = J1939NodeContextBuilder.Build(node, streams);
        J1939NodeEvents.Add(c, events);
        var health = NetworkPeriodicityAnalyzer.DetermineNodeHealth(c.Streams, captureEnd);
        var span = node.Frames[^1].Frame.Timestamp - node.Frames[0].Frame.Timestamp;
        var evidence = new List<string>();
        if (c.Identity is not null) evidence.Add("Address Claim / NAME");
        if (c.Pgns.Count >= 2) evidence.Add($"PGN {c.Pgns.Count}");
        if (c.Peers.Count > 0) evidence.Add($"PDU1 peers {c.Peers.Count}");
        if (c.Streams.Length > 0) evidence.Add($"Periodic streams {c.Streams.Length}");
        if (c.DistinctNames.Length > 1) evidence.Add("Address conflict");

        return new NetworkNodeSnapshot
        {
            NodeKey = $"J1939:{node.SourceAddress:X2}",
            Protocol = c.Identity is not null || protocol.Estimate is NetworkProtocolEstimate.J1939Likely or NetworkProtocolEstimate.MixedOrGateway ? NetworkNodeProtocol.J1939 : NetworkNodeProtocol.GenericExtended,
            Address = node.SourceAddress,
            AddressText = $"SA 0x{node.SourceAddress:X2}",
            Identity = c.Identity is null ? "unconfirmed" : $"NAME {c.Identity.RawHex}",
            State = node.SourceAddress == 0xFE ? "CANNOT CLAIM" : health.ToString().ToUpperInvariant(),
            FirstSeen = node.Frames[0].Frame.Timestamp,
            LastSeen = node.Frames[^1].Frame.Timestamp,
            FrameCount = node.Frames.Count,
            AverageFrequencyHertz = span.TotalSeconds > 0 ? node.Frames.Count / span.TotalSeconds : 0,
            PeriodicityQuality = c.Streams.Length == 0 ? 0 : c.Streams.Average(s => Math.Clamp(100 - (s.JitterPercent ?? 100), 0, 100)),
            PgnOrCobIds = c.Pgns,
            DirectedPeers = c.Peers,
            Health = health,
            Confidence = c.Identity is not null ? 1.0 : Math.Clamp(Math.Max(0.25, protocol.Confidence), 0, 0.9),
            Evidence = evidence,
            J1939Name = c.Identity
        };
    }
}
