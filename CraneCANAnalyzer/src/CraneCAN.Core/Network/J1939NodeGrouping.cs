using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal sealed record J1939ObservedFrame(CanFrame Frame, J1939FrameInfo Info);
internal sealed record J1939ObservedNode(byte SourceAddress, IReadOnlyList<J1939ObservedFrame> Frames);

internal static class J1939NodeGrouping
{
    public static IReadOnlyList<J1939ObservedNode> Group(IReadOnlyList<CanFrame> frames)
    {
        var parsed = new List<J1939ObservedFrame>();
        foreach (var frame in frames.Where(frame => frame.IsExtended))
            if (J1939PassiveParser.TryParse(frame, out var info))
                parsed.Add(new J1939ObservedFrame(frame, info));

        return parsed.GroupBy(item => item.Info.SourceAddress)
            .Select(group => new J1939ObservedNode(group.Key, group.OrderBy(item => item.Frame.Timestamp).ToArray()))
            .ToArray();
    }
}
