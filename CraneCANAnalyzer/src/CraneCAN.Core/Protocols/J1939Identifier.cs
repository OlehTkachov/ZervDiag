namespace CraneCAN.Core.Protocols;

public sealed record J1939Identifier(
    uint CanId,
    int Priority,
    bool ExtendedDataPage,
    bool DataPage,
    int PduFormat,
    int PduSpecific,
    int SourceAddress,
    int Pgn,
    bool IsPdu1,
    int? DestinationAddress)
{
    public string PgnHex => $"0x{Pgn:X5}";
    public string SourceAddressHex => $"0x{SourceAddress:X2}";
    public string? DestinationAddressHex =>
        DestinationAddress.HasValue
            ? $"0x{DestinationAddress.Value:X2}"
            : null;
}

/// <summary>
/// Decodes the SAE J1939 fields carried by a 29-bit CAN identifier.
/// PGN follows the 18-bit EDP/DP/PF/PS layout; for PDU1 (PF &lt; 240)
/// the destination byte is excluded from PGN and the PGN low byte is zero.
/// </summary>
public static class J1939IdentifierCodec
{
    public const uint MaximumExtendedCanId = 0x1FFFFFFFu;

    public static J1939Identifier Decode(uint canId)
    {
        if (canId > MaximumExtendedCanId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canId),
                "J1939 CAN ID должен помещаться в 29 бит.");
        }

        var priority = (int)((canId >> 26) & 0x07);
        var extendedDataPage = ((canId >> 25) & 0x01) != 0;
        var dataPage = ((canId >> 24) & 0x01) != 0;
        var pduFormat = (int)((canId >> 16) & 0xFF);
        var pduSpecific = (int)((canId >> 8) & 0xFF);
        var sourceAddress = (int)(canId & 0xFF);
        var isPdu1 = pduFormat < 240;

        var pgn =
            (extendedDataPage ? 1 << 17 : 0) |
            (dataPage ? 1 << 16 : 0) |
            (pduFormat << 8);
        if (!isPdu1)
            pgn |= pduSpecific;

        return new J1939Identifier(
            canId,
            priority,
            extendedDataPage,
            dataPage,
            pduFormat,
            pduSpecific,
            sourceAddress,
            pgn,
            isPdu1,
            isPdu1 ? pduSpecific : null);
    }

    public static bool SamePgn(uint leftCanId, uint rightCanId) =>
        Decode(leftCanId).Pgn == Decode(rightCanId).Pgn;
}
