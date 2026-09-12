namespace CraneCAN.Core.Network;

internal sealed record J1939NodeContext(
    J1939ObservedNode Node,
    List<string> Pgns,
    List<string> Peers,
    J1939NameInfo? Identity,
    ulong[] DistinctNames,
    J1939ObservedFrame[] Claims,
    NetworkPeriodicStreamSnapshot[] Streams);

internal static class J1939NodeContextBuilder
{
    public static J1939NodeContext Build(J1939ObservedNode node, IReadOnlyList<NetworkPeriodicStreamSnapshot> streams)
    {
        var pgns = node.Frames.Select(x => J1939PassiveParser.FormatPgn(x.Info.Pgn)).Distinct().OrderBy(x => x).ToList();
        var peers = node.Frames.Where(x => x.Info.DestinationAddress.HasValue)
            .Select(x => $"0x{x.Info.DestinationAddress!.Value:X2}").Distinct().OrderBy(x => x).ToList();
        var claims = node.Frames.Where(x => x.Info.IsAddressClaim && x.Frame.Data.Length >= 8).ToArray();
        var names = new List<J1939NameInfo>();
        foreach (var claim in claims)
            if (J1939PassiveParser.TryDecodeAddressClaim(claim.Frame, out var name)) names.Add(name);
        var nodeStreams = streams.Where(x => x.SourceAddress == node.SourceAddress && x.IsPeriodic).ToArray();
        return new J1939NodeContext(node, pgns, peers, names.FirstOrDefault(), names.Select(x => x.RawValue).Distinct().ToArray(), claims, nodeStreams);
    }
}
