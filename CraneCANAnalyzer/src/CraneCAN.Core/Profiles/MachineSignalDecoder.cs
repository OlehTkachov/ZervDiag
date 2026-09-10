namespace CraneCAN.Core.Profiles;

public sealed record DecodedMachineSignalValue(
    ulong RawUnsigned,
    long? RawSigned,
    double EngineeringValue);

public static class MachineSignalDecoder
{
    /// <summary>
    /// Returns the minimum DATA length required to contain the complete field.
    /// LittleEndian uses increasing absolute bit numbers. BigEndian uses the
    /// DBC/Motorola sawtooth convention: StartByte/StartBit is the signal MSB,
    /// bits descend to bit 0, then continue at bit 7 of the next DATA byte.
    /// </summary>
    public static int RequiredDataLength(MachineSignal signal)
    {
        ValidateDefinition(signal);
        return signal.ByteOrder switch
        {
            SignalByteOrder.LittleEndian => RequiredLittleEndianLength(signal),
            SignalByteOrder.BigEndian => RequiredBigEndianLength(signal),
            _ => throw new NotSupportedException(
                $"Неизвестный ByteOrder: {signal.ByteOrder}.")
        };
    }

    public static bool CoversDataByte(MachineSignal signal, int dataIndex)
    {
        ValidateDefinition(signal);
        if (dataIndex is < 0 or > 7)
            return false;

        return signal.ByteOrder switch
        {
            SignalByteOrder.LittleEndian =>
                CoversLittleEndianByte(signal, dataIndex),
            SignalByteOrder.BigEndian =>
                dataIndex >= signal.StartByte &&
                dataIndex < RequiredBigEndianLength(signal),
            _ => false
        };
    }

    public static DecodedMachineSignalValue Decode(
        MachineSignal signal,
        ReadOnlySpan<byte> data)
    {
        ValidateDefinition(signal);

        var requiredBytes = RequiredDataLength(signal);
        if (data.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Для сигнала «{signal.Name}» требуется минимум {requiredBytes} DATA-байт, получено {data.Length}.",
                nameof(data));
        }

        var rawUnsigned = signal.ByteOrder switch
        {
            SignalByteOrder.LittleEndian =>
                DecodeLittleEndian(signal, data, requiredBytes),
            SignalByteOrder.BigEndian =>
                DecodeBigEndian(signal, data),
            _ => throw new NotSupportedException(
                $"Неизвестный ByteOrder: {signal.ByteOrder}.")
        };

        long? rawSigned = null;
        double rawNumeric;
        if (signal.IsSigned)
        {
            long signed;
            if (signal.BitLength == 64)
            {
                signed = unchecked((long)rawUnsigned);
            }
            else
            {
                var signBit = 1UL << (signal.BitLength - 1);
                signed = (rawUnsigned & signBit) == 0
                    ? (long)rawUnsigned
                    : unchecked((long)(rawUnsigned | (~0UL << signal.BitLength)));
            }

            rawSigned = signed;
            rawNumeric = signed;
        }
        else
        {
            rawNumeric = rawUnsigned;
        }

        var engineering = rawNumeric * signal.Scale + signal.Offset;
        if (!double.IsFinite(engineering))
        {
            throw new OverflowException(
                $"Декодированное значение сигнала «{signal.Name}» вышло за диапазон double.");
        }

        return new DecodedMachineSignalValue(
            rawUnsigned,
            rawSigned,
            engineering);
    }

    public static void ValidateDefinition(MachineSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (signal.StartByte is < 0 or > 7)
            throw new ArgumentOutOfRangeException(
                nameof(signal.StartByte),
                "StartByte должен быть в диапазоне 0…7.");
        if (signal.StartBit is < 0 or > 7)
            throw new ArgumentOutOfRangeException(
                nameof(signal.StartBit),
                "StartBit должен быть в диапазоне 0…7.");
        if (signal.BitLength is < 1 or > 64)
            throw new ArgumentOutOfRangeException(
                nameof(signal.BitLength),
                "BitLength должен быть в диапазоне 1…64.");

        switch (signal.ByteOrder)
        {
            case SignalByteOrder.LittleEndian:
            {
                var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
                var lastBitExclusive = checked(firstBit + signal.BitLength);
                if (lastBitExclusive > 64)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(signal.BitLength),
                        "LittleEndian поле выходит за пределы Classical CAN DATA[0…7].");
                }
                break;
            }

            case SignalByteOrder.BigEndian:
            {
                var requiredBytes = RequiredBigEndianLengthUnchecked(signal);
                if (requiredBytes > 8)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(signal.BitLength),
                        "BigEndian/Motorola поле по DBC sawtooth convention выходит за пределы Classical CAN DATA[0…7].");
                }
                break;
            }

            default:
                throw new NotSupportedException(
                    $"Неизвестный ByteOrder: {signal.ByteOrder}.");
        }

        var maximumId = signal.IsExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (signal.CanId > maximumId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signal.CanId),
                "CAN ID не соответствует Standard/Extended формату.");
        }

        if (!double.IsFinite(signal.Scale) || signal.Scale == 0)
            throw new ArgumentOutOfRangeException(
                nameof(signal.Scale),
                "Scale должен быть конечным ненулевым числом.");
        if (!double.IsFinite(signal.Offset))
            throw new ArgumentOutOfRangeException(
                nameof(signal.Offset),
                "Offset должен быть конечным числом.");
    }

    private static int RequiredLittleEndianLength(MachineSignal signal)
    {
        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var lastBitExclusive = checked(firstBit + signal.BitLength);
        return (lastBitExclusive + 7) / 8;
    }

    private static int RequiredBigEndianLength(MachineSignal signal) =>
        RequiredBigEndianLengthUnchecked(signal);

    private static int RequiredBigEndianLengthUnchecked(MachineSignal signal)
    {
        var bitsInFirstByte = signal.StartBit + 1;
        if (signal.BitLength <= bitsInFirstByte)
            return signal.StartByte + 1;

        var remainingBits = signal.BitLength - bitsInFirstByte;
        var additionalBytes = (remainingBits + 7) / 8;
        return checked(signal.StartByte + 1 + additionalBytes);
    }

    private static bool CoversLittleEndianByte(
        MachineSignal signal,
        int dataIndex)
    {
        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var lastBitExclusive = checked(firstBit + signal.BitLength);
        var firstByte = firstBit / 8;
        var lastByte = (lastBitExclusive - 1) / 8;
        return dataIndex >= firstByte && dataIndex <= lastByte;
    }

    private static ulong DecodeLittleEndian(
        MachineSignal signal,
        ReadOnlySpan<byte> data,
        int requiredBytes)
    {
        ulong payload = 0;
        for (var index = 0; index < requiredBytes; index++)
            payload |= (ulong)data[index] << (index * 8);

        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var mask = signal.BitLength == 64
            ? ulong.MaxValue
            : (1UL << signal.BitLength) - 1UL;
        return (payload >> firstBit) & mask;
    }

    private static ulong DecodeBigEndian(
        MachineSignal signal,
        ReadOnlySpan<byte> data)
    {
        ulong raw = 0;
        var byteIndex = signal.StartByte;
        var bitIndex = signal.StartBit;

        for (var index = 0; index < signal.BitLength; index++)
        {
            raw = (raw << 1) |
                  (ulong)((data[byteIndex] >> bitIndex) & 0x01);

            if (bitIndex == 0)
            {
                byteIndex++;
                bitIndex = 7;
            }
            else
            {
                bitIndex--;
            }
        }

        return raw;
    }
}
