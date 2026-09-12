using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

public sealed record J1939FrameInfo
{
    public uint Id { get; init; }
    public byte Priority { get; init; }
    public bool ExtendedDataPage { get; init; }
    public bool DataPage { get; init; }
    public byte PduFormat { get; init; }
    public byte PduSpecific { get; init; }
    public byte SourceAddress { get; init; }
    public byte? DestinationAddress { get; init; }
    public uint Pgn { get; init; }
    public bool IsPdu1 => PduFormat < 0xF0;
    public bool IsAddressClaim => Pgn == J1939PassiveParser.AddressClaimPgn;
    public bool IsRequest => Pgn == J1939PassiveParser.RequestPgn;
}

public static class J1939PassiveParser
{
    public const uint AddressClaimPgn = 0xEE00;
    public const uint RequestPgn = 0xEA00;
    public const byte CannotClaimSourceAddress = 0xFE;

    public static bool TryParse(CanFrame frame, out J1939FrameInfo info)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return TryParse(frame.Id, frame.Protocol == BusProtocol.ClassicalCan && frame.IsExtended, out info);
    }

    public static bool TryParse(uint id, bool isExtended, out J1939FrameInfo info)
    {
        if (!isExtended || id > 0x1FFFFFFF)
        {
            info = default!;
            return false;
        }

        var priority = (byte)((id >> 26) & 0x07);
        var edp = ((id >> 25) & 0x01) != 0;
        var dp = ((id >> 24) & 0x01) != 0;
        var pf = (byte)((id >> 16) & 0xFF);
        var ps = (byte)((id >> 8) & 0xFF);
        var sa = (byte)(id & 0xFF);

        uint pgn = ((id >> 8) & 0x3FFFFu);
        if (pf < 0xF0)
            pgn &= 0x3FF00u;

        info = new J1939FrameInfo
        {
            Id = id,
            Priority = priority,
            ExtendedDataPage = edp,
            DataPage = dp,
            PduFormat = pf,
            PduSpecific = ps,
            SourceAddress = sa,
            DestinationAddress = pf < 0xF0 ? ps : null,
            Pgn = pgn
        };
        return true;
    }

    public static bool TryDecodeAddressClaim(CanFrame frame, out J1939NameInfo name)
    {
        if (!TryParse(frame, out var info) || !info.IsAddressClaim || frame.Data.Length < 8)
        {
            name = default!;
            return false;
        }

        ulong raw = 0;
        for (var i = 0; i < 8; i++)
            raw |= (ulong)frame.Data[i] << (8 * i);

        name = DecodeName(raw);
        return true;
    }

    public static J1939NameInfo DecodeName(ulong raw) => new()
    {
        RawValue = raw,
        IdentityNumber = (uint)(raw & 0x1FFFFF),
        ManufacturerCode = (ushort)((raw >> 21) & 0x7FF),
        EcuInstance = (byte)((raw >> 32) & 0x07),
        FunctionInstance = (byte)((raw >> 35) & 0x1F),
        Function = (byte)((raw >> 40) & 0xFF),
        VehicleSystem = (byte)((raw >> 49) & 0x7F),
        VehicleSystemInstance = (byte)((raw >> 56) & 0x0F),
        IndustryGroup = (byte)((raw >> 60) & 0x07),
        ArbitraryAddressCapable = ((raw >> 63) & 0x01) != 0
    };

    public static string FormatPgn(uint pgn) => pgn <= 0xFFFF
        ? $"0x{pgn:X4}"
        : $"0x{pgn:X5}";
}
