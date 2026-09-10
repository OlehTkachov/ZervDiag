namespace CraneCAN.Core.Profiles;

public sealed record DecodedMachineSignalValue(
    ulong RawUnsigned,
    long? RawSigned,
    double EngineeringValue);

public static class MachineSignalDecoder
{
    public static int RequiredDataLength(MachineSignal signal)
    {
        ValidateDefinition(signal);
        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var lastBitExclusive = checked(firstBit + signal.BitLength);
        return (lastBitExclusive + 7) / 8;
    }

    public static bool CoversDataByte(MachineSignal signal, int dataIndex)
    {
        ValidateDefinition(signal);
        if (dataIndex is < 0 or > 7)
            return false;

        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var lastBitExclusive = checked(firstBit + signal.BitLength);
        var firstByte = firstBit / 8;
        var lastByte = (lastBitExclusive - 1) / 8;
        return dataIndex >= firstByte && dataIndex <= lastByte;
    }

    public static DecodedMachineSignalValue Decode(
        MachineSignal signal,
        ReadOnlySpan<byte> data)
    {
        ValidateDefinition(signal);

        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var lastBitExclusive = checked(firstBit + signal.BitLength);
        var requiredBytes = (lastBitExclusive + 7) / 8;
        if (data.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Для сигнала «{signal.Name}» требуется минимум {requiredBytes} DATA-байт, получено {data.Length}.",
                nameof(data));
        }

        ulong payload = 0;
        for (var index = 0; index < requiredBytes; index++)
            payload |= (ulong)data[index] << (index * 8);

        var mask = signal.BitLength == 64
            ? ulong.MaxValue
            : (1UL << signal.BitLength) - 1UL;
        var rawUnsigned = (payload >> firstBit) & mask;

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

        if (signal.ByteOrder != SignalByteOrder.LittleEndian)
        {
            throw new NotSupportedException(
                "BigEndian/Motorola декодирование намеренно не выполняется: " +
                "в Machine Profile пока не определена однозначная конвенция нумерации битов. " +
                "CraneCAN не будет угадывать семантику.");
        }

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

        var firstBit = checked(signal.StartByte * 8 + signal.StartBit);
        var lastBitExclusive = checked(firstBit + signal.BitLength);
        if (lastBitExclusive > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signal.BitLength),
                "Поле выходит за пределы Classical CAN DATA[0…7].");
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
}
