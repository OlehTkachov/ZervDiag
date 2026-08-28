namespace CraneCAN.Core.Protocols;

public static class Onk160Interpreter
{
    public static string Describe(byte header, ReadOnlySpan<byte> payload, bool? checksumValid)
    {
        if (checksumValid == false)
        {
            return "Ошибка контрольной суммы — пакет показан без расшифровки";
        }

        return header switch
        {
            0xCA => "Системное состояние БОИ",
            0xE5 => DescribeShortMeasurement("Канал E5", payload),
            0xE6 => DescribeRodPressure(payload),
            0xE8 => "Составной блок измерений и состояния E8",
            0xF7 => DescribeF7(payload),
            0x2A => "Одиночный диагностический/повторный байт; наблюдался при E55",
            _ => $"Неизвестный одиночный байт/заголовок 0x{header:X2}"
        };
    }

    public static ushort? ReadRodPressureRaw(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return null;
        }

        return (ushort)(payload[0] | (payload[1] << 8));
    }

    public static byte? ReadF7Status(ReadOnlySpan<byte> payload) =>
        payload.Length < 1 ? null : payload[0];

    private static string DescribeRodPressure(ReadOnlySpan<byte> payload)
    {
        var raw = ReadRodPressureRaw(payload);
        if (!raw.HasValue)
        {
            return "Пакет E6 неполный";
        }

        if (raw.Value == 0x8000)
        {
            return "E31: неисправность датчика давления штоковой полости (raw 0x8000)";
        }

        if ((raw.Value & 0x8000) != 0)
        {
            return $"Датчик штоковой полости: аварийное значение raw 0x{raw.Value:X4}";
        }

        return $"Датчик давления штоковой полости: raw 0x{raw.Value:X4} ({raw.Value})";
    }

    private static string DescribeShortMeasurement(string name, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return $"{name}: пакет неполный";
        }

        var raw = (ushort)(payload[0] | (payload[1] << 8));
        return $"{name}: raw 0x{raw:X4} ({raw}); назначение уточняется";
    }

    private static string DescribeF7(ReadOnlySpan<byte> payload)
    {
        var status = ReadF7Status(payload);
        if (!status.HasValue)
        {
            return "Пакет F7 неполный";
        }

        if (status.Value == 0x10)
        {
            return "Предварительный эталон E55: контроллер оголовка стрелы не отвечает";
        }

        var boomHeadPresent = (status.Value & 0x01) != 0;
        var hookLimitNormal = (status.Value & 0x80) != 0;
        if (boomHeadPresent && !hookLimitNormal)
        {
            return "E83: концевой выключатель подъёма крюка сработал/цепь разомкнута";
        }

        if (boomHeadPresent && hookLimitNormal)
        {
            return "Контроллер оголовка и концевик подъёма крюка: норма";
        }

        return $"Дискретное состояние F7: 0x{status.Value:X2}; расшифровка уточняется";
    }
}
