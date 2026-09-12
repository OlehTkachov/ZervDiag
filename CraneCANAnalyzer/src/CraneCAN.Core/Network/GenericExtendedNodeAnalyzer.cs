using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class GenericExtendedNodeAnalyzer
{
    public static IReadOnlyList<NetworkNodeSnapshot> Build(IReadOnlyList<CanFrame> frames)
    {
        return frames
            .Where(frame => frame.IsExtended)
            .GroupBy(frame => (byte)(frame.Id & 0xFF))
            .Select(BuildNode)
            .OrderBy(node => node.Address)
            .ToArray();
    }

    private static NetworkNodeSnapshot BuildNode(IGrouping<byte, CanFrame> group)
    {
        var ordered = group.OrderBy(frame => frame.Timestamp).ToArray();
        var span = ordered[^1].Timestamp - ordered[0].Timestamp;
        var ids = ordered.Select(frame => frame.Id).Distinct().OrderBy(id => id).ToArray();
        return new NetworkNodeSnapshot
        {
            NodeKey = $"GENERIC_EXT:{group.Key:X2}",
            Protocol = NetworkNodeProtocol.GenericExtended,
            Address = group.Key,
            AddressText = $"suffix 0x{group.Key:X2}",
            Identity = "Extended-ID suffix; node identity unconfirmed",
            State = "UNCONFIRMED",
            FirstSeen = ordered[0].Timestamp,
            LastSeen = ordered[^1].Timestamp,
            FrameCount = ordered.LongLength,
            AverageFrequencyHertz = span.TotalSeconds > 0 ? ordered.Length / span.TotalSeconds : 0,
            PeriodicityQuality = 0,
            PgnOrCobIds = ids.Take(64).Select(id => $"0x{id:X8}").ToList(),
            DirectedPeers = [],
            Health = NetworkNodeHealthState.Unknown,
            Confidence = 0.25,
            Evidence = ["Grouped by low byte only; J1939 node identity is not confirmed."]
        };
    }
}
