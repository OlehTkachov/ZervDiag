using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal sealed record CanopenObservedFrame(CanFrame Frame, CanopenFrameInfo Info);
internal sealed record CanopenObservedNode(byte NodeId, IReadOnlyList<CanopenObservedFrame> Frames);

internal static class CanopenNodeGrouping
{
    public static IReadOnlyList<CanopenObservedNode> Group(IReadOnlyList<CanFrame> frames)
    {
        var parsed = new List<CanopenObservedFrame>();
        foreach (var frame in frames.Where(frame => !frame.IsExtended))
            if (CanopenPassiveParser.TryParse(frame, out var info) && info.NodeId.HasValue)
                parsed.Add(new CanopenObservedFrame(frame, info));

        return parsed.GroupBy(item => item.Info.NodeId!.Value)
            .Select(group => new CanopenObservedNode(group.Key, group.OrderBy(item => item.Frame.Timestamp).ToArray()))
            .Where(node => node.Frames.Any(item => item.Info.Kind is CanopenObjectKind.Heartbeat or CanopenObjectKind.BootUp) || node.Frames.Select(item => item.Frame.Id).Distinct().Count() >= 2)
            .ToArray();
    }
}
