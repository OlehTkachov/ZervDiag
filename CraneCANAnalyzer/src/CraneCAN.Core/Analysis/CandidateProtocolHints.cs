using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Analysis;

public sealed record J1939IdentifierInfo(
    uint Pgn,
    byte SourceAddress,
    byte PduFormat,
    byte PduSpecific)
{
    public string PgnText => Pgn <= 0xFFFF
        ? $"0x{Pgn:X4}"
        : $"0x{Pgn:X5}";

    public string SourceAddressText => $"0x{SourceAddress:X2}";
}

/// <summary>
/// Adds conservative protocol hints to Guided Diagnostics candidates without
/// claiming that an unknown extended CAN bus is definitely J1939.
/// </summary>
public static class CandidateProtocolHints
{
    public static bool TryDecodeJ1939(
        uint id,
        bool isExtended,
        out J1939IdentifierInfo info)
    {
        if (!isExtended || id > 0x1FFFFFFF)
        {
            info = default!;
            return false;
        }

        var pduFormat = (byte)((id >> 16) & 0xFF);
        var pduSpecific = (byte)((id >> 8) & 0xFF);
        var sourceAddress = (byte)(id & 0xFF);

        // J1939 PGN is the 18-bit field above Source Address. For PDU1
        // (PF < 240), PS is the destination address and is not part of PGN.
        var pgn = (id >> 8) & 0x3FFFFu;
        if (pduFormat < 0xF0)
            pgn &= 0x3FF00u;

        info = new J1939IdentifierInfo(
            pgn,
            sourceAddress,
            pduFormat,
            pduSpecific);
        return true;
    }

    public static string Classify(GuidedCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.BitIndex.HasValue)
            return "дискретный";

        return candidate.ChangeKind switch
        {
            CandidateChangeKind.StableBit => "дискретный",
            CandidateChangeKind.StableByte => IsSingleBitTransition(candidate)
                ? "дискретный?"
                : "числовой / enum?",
            CandidateChangeKind.Ramp => "аналоговый",
            CandidateChangeKind.AnalogNoise => "аналоговый / шум",
            CandidateChangeKind.MessageAppeared => "ID появился",
            CandidateChangeKind.MessageDisappeared => "ID исчез",
            CandidateChangeKind.DlcChanged => "структура / DLC",
            _ => "не определён"
        };
    }

    private static bool IsSingleBitTransition(GuidedCandidate candidate)
    {
        if (!candidate.ReferenceValue.HasValue || !candidate.ActionValue.HasValue)
            return false;

        var xor = candidate.ReferenceValue.Value ^ candidate.ActionValue.Value;
        return xor != 0 && (xor & (xor - 1)) == 0;
    }
}
