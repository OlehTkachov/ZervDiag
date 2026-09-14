using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class J1939ProtocolScorer
{
    private const uint TransportProtocolConnectionManagementPgn = 0xEC00;
    private const uint TransportProtocolDataTransferPgn = 0xEB00;

    public static double Score(IReadOnlyList<CanFrame> frames, IReadOnlyList<NetworkPeriodicStreamSnapshot> streams, ICollection<NetworkEvidence> evidence)
    {
        var items = frames
            .Where(f => f.IsExtended)
            .Select(f => (Frame: f, Info: Parse(f)))
            .Where(x => x.Info is not null)
            .ToArray();

        var score = 0.0;

        var claims = items.Count(x => x.Info!.IsAddressClaim && x.Frame.Data.Length >= 8);
        if (claims > 0)
        {
            score += 0.55;
            evidence.Add(E("J1939_ADDRESS_CLAIM", $"Address Claim: {claims}", 0.55));
        }

        var transportSources = items
            .Where(IsPlausibleTpCm)
            .Select(x => x.Info!.SourceAddress)
            .Distinct()
            .Intersect(items.Where(IsPlausibleTpDt).Select(x => x.Info!.SourceAddress).Distinct())
            .OrderBy(x => x)
            .ToArray();
        if (transportSources.Length > 0)
        {
            score += 0.55;
            evidence.Add(E(
                "J1939_TP_SESSION",
                $"Согласованные TP.CM + TP.DT для SA: {string.Join(", ", transportSources.Select(sa => $"0x{sa:X2}"))}",
                0.55));
        }

        var requests = items.Count(x => x.Info!.IsRequest);
        if (requests > 0)
        {
            score += 0.15;
            evidence.Add(E("J1939_REQUEST", $"Request PGN: {requests}", 0.15));
        }

        var coherent = items
            .GroupBy(x => x.Info!.SourceAddress)
            .Count(g => g.Select(x => x.Info!.Pgn).Distinct().Count() >= 2);
        if (coherent >= 2)
        {
            score += 0.15;
            evidence.Add(E("J1939_COHERENT_SA", $"SA с несколькими PGN: {coherent}", 0.15));
        }

        var directed = items.Count(x => x.Info!.DestinationAddress.HasValue);
        if (directed >= 5)
        {
            score += 0.10;
            evidence.Add(E("J1939_PDU1", $"PDU1 кадров: {directed}", 0.10));
        }

        if (streams.Count(s => s.IsExtended && s.IsPeriodic) >= 2)
        {
            score += 0.05;
            evidence.Add(E("J1939_PERIODIC_EXT", "Периодический Extended-трафик", 0.05));
        }

        return Math.Clamp(score, 0, 1);
    }

    private static bool IsPlausibleTpCm((CanFrame Frame, J1939FrameInfo? Info) item)
    {
        if (item.Info?.Pgn != TransportProtocolConnectionManagementPgn || item.Frame.Data.Length < 8)
            return false;

        return item.Frame.Data[0] is 0x10 or 0x11 or 0x13 or 0x20 or 0xFF;
    }

    private static bool IsPlausibleTpDt((CanFrame Frame, J1939FrameInfo? Info) item) =>
        item.Info?.Pgn == TransportProtocolDataTransferPgn &&
        item.Frame.Data.Length >= 2 &&
        item.Frame.Data[0] != 0;

    private static J1939FrameInfo? Parse(CanFrame frame) =>
        J1939PassiveParser.TryParse(frame, out var info) ? info : null;

    private static NetworkEvidence E(string code, string description, double weight) => new()
    {
        Code = code,
        Description = description,
        Weight = weight
    };
}
