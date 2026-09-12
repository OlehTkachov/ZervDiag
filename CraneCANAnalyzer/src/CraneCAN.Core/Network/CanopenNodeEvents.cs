namespace CraneCAN.Core.Network;

internal static class CanopenNodeEvents
{
    public static void Add(CanopenObservedNode node, ICollection<NetworkEvent> events)
    {
        var key = $"CANOPEN:{node.NodeId:X2}";
        events.Add(new NetworkEvent
        {
            Kind = NetworkEventKind.NodeAppeared,
            Timestamp = node.Frames[0].Frame.Timestamp,
            NodeKey = key,
            Id = node.Frames[0].Frame.Id,
            IsExtended = false,
            Description = $"CANopen Node {node.NodeId} first seen"
        });

        byte? previous = null;
        foreach (var item in node.Frames.Where(x => x.Info.Kind is CanopenObjectKind.Heartbeat or CanopenObjectKind.BootUp))
        {
            if (item.Info.Kind == CanopenObjectKind.BootUp)
                events.Add(new NetworkEvent
                {
                    Kind = NetworkEventKind.CanopenBootUp,
                    Timestamp = item.Frame.Timestamp,
                    NodeKey = key,
                    Id = item.Frame.Id,
                    IsExtended = false,
                    Description = $"CANopen Node {node.NodeId} Boot-up"
                });
            else if (item.Info.NmtState.HasValue && item.Info.NmtState != previous)
            {
                events.Add(new NetworkEvent
                {
                    Kind = NetworkEventKind.CanopenStateChanged,
                    Timestamp = item.Frame.Timestamp,
                    NodeKey = key,
                    Id = item.Frame.Id,
                    IsExtended = false,
                    Description = $"CANopen Node {node.NodeId}: {item.Info.NmtStateText}"
                });
                previous = item.Info.NmtState;
            }
        }
    }
}
