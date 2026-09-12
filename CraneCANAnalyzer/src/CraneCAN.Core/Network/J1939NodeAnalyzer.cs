using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static partial class J1939NodeAnalyzer
{
    public static IReadOnlyList<NetworkNodeSnapshot> Build(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<NetworkPeriodicStreamSnapshot> streams,
        NetworkProtocolEstimateResult protocol,
        ICollection<NetworkEvent> events)
    {
        var groups = J1939NodeGrouping.Group(frames);
        return groups.Select(group => CreateNode(group, frames[^1].Timestamp, streams, protocol, events)).ToArray();
    }
}
