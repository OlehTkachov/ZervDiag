using CraneCAN.Core.Models;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Core.Profiles;

public sealed record DecodedJ1939SignalValue(
    J1939Identifier Identifier,
    int? Spn,
    ulong RawUnsigned,
    long? RawSigned,
    double EngineeringValue);

public static class J1939SignalDecoder
{
    public static bool MatchesIdentifier(
        MachineSignal signal,
        uint observedCanId,
        bool isExtended)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (signal.IsExtended != isExtended)
            return false;

        if (signal.CanId == observedCanId)
            return true;

        if (!isExtended ||
            !signal.J1939Pgn.HasValue ||
            !string.Equals(
                signal.Protocol,
                "J1939",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected =
            J1939IdentifierCodec.Decode(signal.CanId);
        var observed =
            J1939IdentifierCodec.Decode(observedCanId);

        if (observed.Pgn != signal.J1939Pgn.Value ||
            expected.Pgn != signal.J1939Pgn.Value)
        {
            return false;
        }

        // For PDU1 the PS byte is a destination address and is not part of PGN.
        // Keep destination fixed; only the source address may vary automatically.
        return !expected.IsPdu1 ||
               expected.DestinationAddress ==
               observed.DestinationAddress;
    }

    public static DecodedJ1939SignalValue Decode(
        MachineSignal signal,
        CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(frame);

        if (!signal.J1939Pgn.HasValue ||
            !string.Equals(
                signal.Protocol,
                "J1939",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "MachineSignal не содержит явной J1939 PGN metadata.",
                nameof(signal));
        }

        frame.Validate();
        if (frame.Protocol != BusProtocol.ClassicalCan ||
            frame.Direction != CanDirection.Rx ||
            frame.IsRemote ||
            frame.IsError ||
            !frame.IsExtended)
        {
            throw new ArgumentException(
                "J1939 decoder принимает только Rx 29-bit Classical CAN data frame.",
                nameof(frame));
        }

        if (!MatchesIdentifier(
                signal,
                frame.Id,
                frame.IsExtended))
        {
            var observed =
                J1939IdentifierCodec.Decode(frame.Id);
            throw new ArgumentException(
                $"Frame PGN 0x{observed.Pgn:X5} / ID 0x{frame.Id:X8} " +
                $"не соответствует signal PGN 0x{signal.J1939Pgn.Value:X5}.",
                nameof(frame));
        }

        var decoded =
            MachineSignalDecoder.Decode(signal, frame.Data);
        return new DecodedJ1939SignalValue(
            J1939IdentifierCodec.Decode(frame.Id),
            signal.J1939Spn,
            decoded.RawUnsigned,
            decoded.RawSigned,
            decoded.EngineeringValue);
    }
}
