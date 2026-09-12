namespace CraneCAN.Core.Network;

internal static partial class CanopenNodeAnalyzer
{
    public static IReadOnlyList<NetworkNodeSnapshot> Build(
        IReadOnlyList<CraneCAN.Core.Models.CanFrame> frames,
        IReadOnlyList<NetworkPeriodicStreamSnapshot> streams,
        NetworkProtocolEstimateResult protocol,
        ICollection<NetworkEvent> events)
    {
        return CanopenNodeGrouping.Group(frames)
            .Select(node => CreateNode(node, frames[0].Timestamp, frames[^1].Timestamp, streams, protocol, events))
            .ToArray();
    }
}
