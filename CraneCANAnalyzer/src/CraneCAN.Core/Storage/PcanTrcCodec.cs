using System.Globalization;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Storage;

/// <summary>
/// Reader for PEAK-System text trace files (.trc).
///
/// The parser follows the published PEAK CAN TRC file-format layouts for
/// versions 1.0 through 3.0 and imports physical Classical CAN data frames.
/// CAN FD/CAN XL frames, remote requests, status/error records and user events
/// are intentionally ignored because <see cref="CanFrame"/> currently models
/// Classical CAN data frames only.
/// </summary>
public static class PcanTrcCodec
{
    private static readonly Version Version10 = new(1, 0);
    private static readonly Version Version13 = new(1, 3);
    private static readonly Version Version30 = new(3, 0);

    public static async Task<IReadOnlyList<CanFrame>> LoadAsync(
        string path,
        int defaultChannel = 0,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lines.Add(line);
        }

        return Parse(lines, defaultChannel);
    }

    public static IReadOnlyList<CanFrame> Parse(IEnumerable<string> lines, int defaultChannel = 0)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (defaultChannel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultChannel));
        }

        var result = new List<CanFrame>();
        var fileVersion = Version10; // Version 1.0 predates the $FILEVERSION keyword.
        string[]? columns = null;
        var start = DateTimeOffset.UnixEpoch;
        var haveStart = false;

        foreach (var sourceLine in lines)
        {
            var line = sourceLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryReadKeyword(line, "$FILEVERSION", out var fileVersionText))
            {
                if (!Version.TryParse(fileVersionText, out var parsedVersion) ||
                    parsedVersion < Version10 || parsedVersion > Version30)
                {
                    throw new FormatException($"Неподдерживаемая версия PEAK TRC: '{fileVersionText}'. Поддерживаются 1.0-3.0.");
                }

                fileVersion = parsedVersion;
                continue;
            }

            if (TryReadKeyword(line, "$STARTTIME", out var startText))
            {
                if (TryParseStartTime(startText, out var parsedStart))
                {
                    start = parsedStart;
                    haveStart = true;
                }
                continue;
            }

            if (TryReadKeyword(line, "$COLUMNS", out var columnsText))
            {
                columns = columnsText
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                continue;
            }

            if (line[0] == ';' || line[0] == '$')
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            CanFrame? frame;
            var parsed = fileVersion.Major >= 2
                ? TryParseVersion2Plus(tokens, fileVersion, columns, start, haveStart, defaultChannel, out frame)
                : TryParseVersion1(tokens, fileVersion, start, haveStart, defaultChannel, out frame);

            if (parsed && frame is not null)
            {
                frame.Validate();
                result.Add(frame);
            }
        }

        if (result.Count == 0)
        {
            throw new FormatException("В TRC не найдено ни одного обычного Classical CAN data frame Rx/Tx.");
        }

        return result;
    }

    private static bool TryParseVersion1(
        IReadOnlyList<string> tokens,
        Version version,
        DateTimeOffset start,
        bool haveStart,
        int defaultChannel,
        out CanFrame? frame)
    {
        frame = null;

        if (version > Version13)
        {
            throw new FormatException($"Неподдерживаемая версия PEAK TRC 1.x: {version.Major}.{version.Minor}.");
        }

        if (tokens.Count < 4 || !TryParseOffsetMilliseconds(tokens[1], out var offsetMs))
        {
            return false;
        }

        int idIndex;
        int lengthIndex;
        int dataIndex;
        int? busIndex = null;
        var direction = CanDirection.Rx;

        switch (version.Minor)
        {
            case 0:
                // V1.0 has no direction column. For passive trace analysis we
                // treat normal data records as received frames.
                idIndex = 2;
                lengthIndex = 3;
                dataIndex = 4;
                break;

            case 1:
                if (tokens.Count < 5 || !TryParseDirection(tokens[2], out direction))
                {
                    return false; // Warng/Error and malformed records.
                }
                idIndex = 3;
                lengthIndex = 4;
                dataIndex = 5;
                break;

            case 2:
                if (tokens.Count < 6 || !TryParseDirection(tokens[3], out direction))
                {
                    return false;
                }
                busIndex = 2;
                idIndex = 4;
                lengthIndex = 5;
                dataIndex = 6;
                break;

            case 3:
                if (tokens.Count < 7 || !TryParseDirection(tokens[3], out direction))
                {
                    return false;
                }
                busIndex = 2;
                idIndex = 4;
                // tokens[5] is the Reserved/J1939 destination field.
                lengthIndex = 6;
                dataIndex = 7;
                break;

            default:
                return false;
        }

        if (!TryParseCanId(tokens[idIndex], out var id, out var isExtended) ||
            !TryParseClassicalLength(tokens[lengthIndex], out var dataLength) ||
            !TryParseData(tokens, dataIndex, dataLength, out var data))
        {
            return false;
        }

        var channel = defaultChannel;
        if (busIndex.HasValue &&
            !TryMapBusChannel(tokens[busIndex.Value], defaultChannel, out channel))
        {
            return false;
        }

        frame = CreateFrame(start, haveStart, offsetMs, channel, id, isExtended, direction, data);
        return true;
    }

    private static bool TryParseVersion2Plus(
        IReadOnlyList<string> tokens,
        Version version,
        IReadOnlyList<string>? columns,
        DateTimeOffset start,
        bool haveStart,
        int defaultChannel,
        out CanFrame? frame)
    {
        frame = null;

        if (columns is null || columns.Count == 0)
        {
            throw new FormatException($"PEAK TRC {version.Major}.{version.Minor} требует заголовок $COLUMNS.");
        }

        var typeColumn = IndexOfColumn(columns, "T");
        if (typeColumn < 0 || typeColumn >= tokens.Count)
        {
            return false;
        }

        // DT is a normal CAN/J1939 data frame. FD/FB/FE/BI and XL belong to
        // other protocols; RR/ST/ER/EC/EV/PE/OF/EN are not data frames.
        if (!tokens[typeColumn].Equals("DT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokenIndex = 0;
        var dataIndex = -1;

        foreach (var column in columns)
        {
            if (column == "D")
            {
                dataIndex = tokenIndex;
                break;
            }

            if (tokenIndex >= tokens.Count)
            {
                return false;
            }

            values[column] = tokens[tokenIndex++];
        }

        if (dataIndex < 0 ||
            !values.TryGetValue("O", out var offsetText) ||
            !TryParseOffsetMilliseconds(offsetText, out var offsetMs) ||
            !values.TryGetValue("I", out var idText) ||
            !TryParseCanId(idText, out var id, out var isExtended) ||
            !values.TryGetValue("d", out var directionText) ||
            !TryParseDirection(directionText, out var direction))
        {
            return false;
        }

        var lengthText = values.TryGetValue("l", out var actualLength)
            ? actualLength
            : values.TryGetValue("L", out var dlc)
                ? dlc
                : null;

        if (lengthText is null ||
            !TryParseClassicalLength(lengthText, out var dataLength) ||
            !TryParseData(tokens, dataIndex, dataLength, out var data))
        {
            return false;
        }

        var channel = defaultChannel;
        if (values.TryGetValue("B", out var busText) &&
            !TryMapBusChannel(busText, defaultChannel, out channel))
        {
            return false;
        }

        frame = CreateFrame(start, haveStart, offsetMs, channel, id, isExtended, direction, data);
        return true;
    }

    private static CanFrame CreateFrame(
        DateTimeOffset start,
        bool haveStart,
        double offsetMs,
        int channel,
        uint id,
        bool isExtended,
        CanDirection direction,
        byte[] data)
    {
        var timestamp = (haveStart ? start : DateTimeOffset.UnixEpoch).AddMilliseconds(offsetMs);
        return new CanFrame
        {
            Timestamp = timestamp,
            Channel = channel,
            Id = id,
            Data = data,
            Protocol = BusProtocol.ClassicalCan,
            Direction = direction,
            IsExtended = isExtended
        };
    }

    private static bool TryReadKeyword(string line, string keyword, out string value)
    {
        var prefix = ";" + keyword + "=";
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = line[prefix.Length..].Trim().TrimEnd(';').Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseStartTime(string text, out DateTimeOffset start)
    {
        start = DateTimeOffset.UnixEpoch;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var oaDate))
        {
            return false;
        }

        try
        {
            // The TRC format stores an OLE Automation local wall-clock value
            // but no timezone. Use the local timezone of the analysis PC,
            // matching PCAN-View's interpretation of the same trace.
            var local = DateTime.SpecifyKind(DateTime.FromOADate(oaDate), DateTimeKind.Local);
            start = new DateTimeOffset(local).ToUniversalTime();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseOffsetMilliseconds(string token, out double milliseconds)
    {
        token = token.Trim().TrimEnd(')', ':');
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds) &&
               milliseconds >= 0;
    }

    private static bool TryParseDirection(string token, out CanDirection direction)
    {
        if (token.Equals("Rx", StringComparison.OrdinalIgnoreCase))
        {
            direction = CanDirection.Rx;
            return true;
        }

        if (token.Equals("Tx", StringComparison.OrdinalIgnoreCase))
        {
            direction = CanDirection.Tx;
            return true;
        }

        direction = default;
        return false;
    }

    private static bool TryParseCanId(string token, out uint id, out bool isExtended)
    {
        var value = token.Trim().TrimEnd('h', 'H');
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length is < 1 or > 8 || !value.All(Uri.IsHexDigit) ||
            !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
        {
            isExtended = false;
            id = 0;
            return false;
        }

        // PEAK encodes the frame format in the printed CAN-ID width:
        // V1.x/V2.x: standard=4 hex digits, extended=8;
        // V3.0:      standard=3 hex digits, extended=8.
        // The numeric threshold is kept as a tolerant fallback for noncanonical files.
        isExtended = value.Length == 8 || id > 0x7FF;
        return id <= (isExtended ? 0x1FFFFFFFu : 0x7FFu);
    }

    private static bool TryParseClassicalLength(string token, out int length) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out length) &&
        length is >= 0 and <= 8;

    private static bool TryParseData(
        IReadOnlyList<string> tokens,
        int dataIndex,
        int dataLength,
        out byte[] data)
    {
        data = [];
        if (dataIndex < 0 || tokens.Count - dataIndex < dataLength)
        {
            return false;
        }

        var bytes = new byte[dataLength];
        for (var index = 0; index < dataLength; index++)
        {
            var token = tokens[dataIndex + index].Trim().TrimEnd('h', 'H');
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                token = token[2..];
            }

            if (token.Length is < 1 or > 2 ||
                !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
            {
                return false;
            }
        }

        data = bytes;
        return true;
    }

    private static bool TryMapBusChannel(string token, int defaultChannel, out int channel)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bus) ||
            bus is < 1 or > 16)
        {
            channel = defaultChannel;
            return false;
        }

        channel = checked(defaultChannel + bus - 1);
        return true;
    }

    private static int IndexOfColumn(IReadOnlyList<string> columns, string column)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].Equals(column, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
