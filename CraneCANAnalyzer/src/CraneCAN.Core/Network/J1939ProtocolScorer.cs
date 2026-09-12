using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class J1939ProtocolScorer
{
    public static double Score(IReadOnlyList<CanFrame> frames, IReadOnlyList<NetworkPeriodicStreamSnapshot> streams, ICollection<NetworkEvidence> evidence)
    {
        var items = frames.Where(f => f.IsExtended).Select(f => (f, Parse(f))).Where(x => x.Item2 is not null).ToArray();
        var score = 0.0;
        var claims = items.Count(x => x.Item2!.IsAddressClaim && x.f.Data.Length >= 8);
        if (claims > 0) { score += 0.55; evidence.Add(E("J1939_ADDRESS_CLAIM", $"Address Claim: {claims}", 0.55)); }
        var requests = items.Count(x => x.Item2!.IsRequest);
        if (requests > 0) { score += 0.15; evidence.Add(E("J1939_REQUEST", $"Request PGN: {requests}", 0.15)); }
        var coherent = items.GroupBy(x => x.Item2!.SourceAddress).Count(g => g.Select(x => x.Item2!.Pgn).Distinct().Count() >= 2);
        if (coherent >= 2) { score += 0.15; evidence.Add(E("J1939_COHERENT_SA", $"SA с несколькими PGN: {coherent}", 0.15)); }
        var directed = items.Count(x => x.Item2!.DestinationAddress.HasValue);
        if (directed >= 5) { score += 0.10; evidence.Add(E("J1939_PDU1", $"PDU1 кадров: {directed}", 0.10)); }
        if (streams.Count(s => s.IsExtended && s.IsPeriodic) >= 2) { score += 0.05; evidence.Add(E("J1939_PERIODIC_EXT", "Периодический Extended-трафик", 0.05)); }
        return Math.Clamp(score, 0, 1);
    }

    private static J1939FrameInfo? Parse(CanFrame frame) => J1939PassiveParser.TryParse(frame, out var info) ? info : null;
    private static NetworkEvidence E(string code, string description, double weight) => new() { Code = code, Description = description, Weight = weight };
}
