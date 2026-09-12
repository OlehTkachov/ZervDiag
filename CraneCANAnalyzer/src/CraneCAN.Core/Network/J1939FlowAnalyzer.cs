using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class J1939FlowAnalyzer
{
    public static IReadOnlyList<NetworkFlowSnapshot> Build(IReadOnlyList<CanFrame> frames, ICollection<NetworkEvent> events)
    {
        var parsed = frames.Where(f => f.IsExtended).Select(f => (Frame: f, Info: Parse(f))).Where(x => x.Info is not null).ToArray();
        var result = new List<NetworkFlowSnapshot>();
        foreach (var group in parsed.GroupBy(x => (x.Info!.SourceAddress, x.Info.DestinationAddress, Kind: x.Info.IsPdu1 ? "PDU1" : "PDU2")))
        {
            var ordered = group.OrderBy(x => x.Frame.Timestamp).ToArray();
            var span = ordered[^1].Frame.Timestamp - ordered[0].Frame.Timestamp;
            result.Add(new NetworkFlowSnapshot
            {
                SourceAddress = group.Key.SourceAddress,
                DestinationAddress = group.Key.DestinationAddress,
                Kind = group.Key.Kind,
                FrameCount = ordered.LongLength,
                Pgns = ordered.Select(x => J1939PassiveParser.FormatPgn(x.Info!.Pgn)).Distinct().OrderBy(x => x).ToList(),
                FirstSeen = ordered[0].Frame.Timestamp,
                LastSeen = ordered[^1].Frame.Timestamp,
                AverageFrequencyHertz = span.TotalSeconds > 0 ? ordered.Length / span.TotalSeconds : 0
            });
            events.Add(new NetworkEvent
            {
                Kind = NetworkEventKind.FlowAppeared,
                Timestamp = ordered[0].Frame.Timestamp,
                NodeKey = $"J1939:{group.Key.SourceAddress:X2}",
                Id = ordered[0].Frame.Id,
                IsExtended = true,
                Description = group.Key.DestinationAddress.HasValue
                    ? $"SA 0x{group.Key.SourceAddress:X2} -> DA 0x{group.Key.DestinationAddress.Value:X2}"
                    : $"SA 0x{group.Key.SourceAddress:X2} -> PDU2"
            });
        }
        return result;
    }

    private static J1939FrameInfo? Parse(CanFrame frame) => J1939PassiveParser.TryParse(frame, out var info) ? info : null;
}
