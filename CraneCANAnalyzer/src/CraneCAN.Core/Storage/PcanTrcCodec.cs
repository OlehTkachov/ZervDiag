using System.Globalization;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Storage;

public sealed record PcanTrcImportResult(
    IReadOnlyList<CanFrame> Frames,
    int TotalLines,
    int HeaderAndCommentLines,
    int RemoteFramesSkipped,
    int ErrorFramesSkipped,
    int UnknownOrMalformedLines)
{
    public int SkippedLines =>
        HeaderAndCommentLines + RemoteFramesSkipped + ErrorFramesSkipped + UnknownOrMalformedLines;
}

/// <summary>
/// Streaming, order-preserving reader for PEAK-System PCAN-View text traces.
/// The parser follows the published layouts for TRC versions 1.0 through 3.0.
/// Only physical Classical CAN data frames are imported into <see cref="CanFrame"/>;
/// RTR, error/status, CAN FD/CAN XL and unknown records are counted and skipped.
/// </summary>
public static class PcanTrcCodec
{
    public const int DefaultMaximumFrameCount = 1_000_000;

    private static readonly Version Version10 = new(1, 0);
    private static readonly Version Version13 = new(1, 3);
    private static readonly Version Version30 = new(3, 0);

    public static async Task<IReadOnlyList<CanFrame>> LoadAsync(
        string path,
        int defaultChannel = 0,
        CancellationToken cancellationToken = default) =>
        (await LoadWithDiagnosticsAsync(path, defaultChannel, cancellationToken).ConfigureAwait(false)).Frames;

    public static async Task<PcanTrcImportResult> LoadWithDiagnosticsAsync(
        string path,
        int defaultChannel = 0,
        CancellationToken cancellationToken = default,
        int maximumFrameCount = DefaultMaximumFrameCount)
    {
        ValidateArguments(defaultChannel, maximumFrameCount);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Путь к TRC-файлу не задан.", nameof(path));
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var parser = new ParserState(defaultChannel, maximumFrameCount);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parser.Process(line);
        }

        return parser.Complete();
    }

    public static IReadOnlyList<CanFrame> Parse(
        IEnumerable<string> lines,
        int defaultChannel = 0,
        int maximumFrameCount = DefaultMaximumFrameCount) =>
        ParseWithDiagnostics(lines, defaultChannel, maximumFrameCount).Frames;

    public static PcanTrcImportResult ParseWithDiagnostics(
        IEnumerable<string> lines,
        int defaultChannel = 0,
        int maximumFrameCount = DefaultMaximumFrameCount)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ValidateArguments(defaultChannel, maximumFrameCount);
        var parser = new ParserState(defaultChannel, maximumFrameCount);
        foreach (var line in lines)
        {
            parser.Process(line);
        }

        return parser.Complete();
    }

    private static void ValidateArguments(int defaultChannel, int maximumFrameCount)
    {
        if (defaultChannel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultChannel));
        }

        if (maximumFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameCount));
        }
    }

    private sealed class ParserState
    {
        private readonly int _defaultChannel;
        private readonly int _maximumFrameCount;
        private readonly List<CanFrame> _frames = [];
        private Version _fileVersion = Version10;
        private string[]? _columns;
        private DateTimeOffset _start = DateTimeOffset.UnixEpoch;
        private bool _haveStart;

        public ParserState(int defaultChannel, int maximumFrameCount)
        {
            _defaultChannel = defaultChannel;
            _maximumFrameCount = maximumFrameCount;
        }

        public int TotalLines { get; private set; }
        public int HeaderAndCommentLines { get; private set; }
        public int RemoteFramesSkipped { get; private set; }
        public int ErrorFramesSkipped { get; private set; }
        public int UnknownOrMalformedLines { get; private set; }

        public void Process(string? sourceLine)
        {
            TotalLines++;
            var line = (sourceLine ?? string.Empty).Trim();
            if (line.Length == 0)
            {
                HeaderAndCommentLines++;
                return;
            }

            if (TryReadKeyword(line, "$FILEVERSION", out var fileVersionText))
            {
                if (!Version.TryParse(fileVersionText, out var parsedVersion) ||
                    parsedVersion < Version10 || parsedVersion > Version30)
                {
                    throw new FormatException(
                        $"Неподдерживаемая версия PEAK TRC: «{fileVersionText}». Поддерживаются версии 1.0–3.0.");
                }

                _fileVersion = parsedVersion;
                HeaderAndCommentLines++;
                return;
            }

            if (TryReadKeyword(line, "$STARTTIME", out var startText))
            {
                if (TryParseStartTime(startText, out var parsedStart))
                {
                    _start = parsedStart;
                    _haveStart = true;
                }

                HeaderAndCommentLines++;
                return;
            }

            if (TryReadKeyword(line, "$COLUMNS", out var columnsText))
            {
                _columns = columnsText
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                HeaderAndCommentLines++;
                return;
            }

            if (line[0] is ';' or '$')
            {
                HeaderAndCommentLines++;
                return;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (IsRemoteRecord(tokens))
            {
                RemoteFramesSkipped++;
                return;
            }

            CanFrame? frame;
            var parsed = _fileVersion.Major >= 2
                ? TryParseVersion2Plus(
                    tokens, _fileVersion, _columns, _start, _haveStart, _defaultChannel, out frame)
                : TryParseVersion1(
                    tokens, _fileVersion, _start, _haveStart, _defaultChannel, out frame);

            // Some exported or concatenated field traces contain legacy rows
            // under a newer header. Use a constrained fallback only when an
            // explicit Rx/Tx token and a valid Classical CAN layout are found.
            if (!parsed)
            {
                parsed = TryParseTolerantClassic(
                    tokens, _start, _haveStart, _defaultChannel, out frame);
            }

            if (!parsed || frame is null)
            {
                ClassifySkippedRecord(tokens, line);
                return;
            }

            try
            {
                frame.Validate();
            }
            catch (ArgumentException)
            {
                UnknownOrMalformedLines++;
                return;
            }

            if (_frames.Count >= _maximumFrameCount)
            {
                throw new InvalidDataException(
                    $"TRC содержит больше {_maximumFrameCount:N0} обычных CAN-кадров. " +
                    "Импорт остановлен до исчерпания памяти; исходный файл не изменён. " +
                    "Разделите копию трассы в PCAN-View на более короткие интервалы.");
            }

            _frames.Add(frame);
        }

        public PcanTrcImportResult Complete()
        {
            if (_frames.Count == 0)
            {
                throw new FormatException(
                    "TRC-файл не содержит обычных кадров Classical CAN с данными. " +
                    "Комментарии, RTR, error/status, неподдерживаемые типы и повреждённые строки были пропущены.");
            }

            return new PcanTrcImportResult(
                _frames,
                TotalLines,
                HeaderAndCommentLines,
                RemoteFramesSkipped,
                ErrorFramesSkipped,
                UnknownOrMalformedLines);
        }

        private void ClassifySkippedRecord(IReadOnlyList<string> tokens, string line)
        {
            if (IsRemoteRecord(tokens))
            {
                RemoteFramesSkipped++;
            }
            else if (IsErrorStatusOrUnsupportedRecord(tokens, line, _columns))
            {
                ErrorFramesSkipped++;
            }
            else
            {
                UnknownOrMalformedLines++;
            }
        }
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
            throw new FormatException(
                $"Неподдерживаемая версия PEAK TRC 1.x: {version.Major}.{version.Minor}.");
        }

        if (tokens.Count < 4 || !TryParseOffsetMilliseconds(tokens[1], out var offsetMilliseconds))
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
                idIndex = 2;
                lengthIndex = 3;
                dataIndex = 4;
                break;
            case 1:
                if (tokens.Count < 5 || !TryParseDirection(tokens[2], out direction))
                {
                    return false;
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
        if (busIndex.HasValue && !TryMapBusChannel(tokens[busIndex.Value], defaultChannel, out channel))
        {
            return false;
        }

        frame = CreateFrame(
            start, haveStart, offsetMilliseconds, channel, id, isExtended, direction, data);
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
            throw new FormatException(
                $"PEAK TRC {version.Major}.{version.Minor} требует заголовок $COLUMNS.");
        }

        var typeColumn = IndexOfColumn(columns, "T");
        if (typeColumn < 0 || typeColumn >= tokens.Count ||
            !tokens[typeColumn].Equals("DT", StringComparison.OrdinalIgnoreCase))
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
            !TryParseOffsetMilliseconds(offsetText, out var offsetMilliseconds) ||
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

        frame = CreateFrame(
            start, haveStart, offsetMilliseconds, channel, id, isExtended, direction, data);
        return true;
    }

    private static bool TryParseTolerantClassic(
        IReadOnlyList<string> tokens,
        DateTimeOffset start,
        bool haveStart,
        int defaultChannel,
        out CanFrame? frame)
    {
        frame = null;
        if (!TryFindDirection(tokens, out var directionIndex, out var direction))
        {
            return false;
        }

        var candidates = new[]
        {
            directionIndex + 1,
            directionIndex - 1,
            directionIndex + 2,
            directionIndex - 2,
            directionIndex + 3,
            directionIndex - 3
        };

        foreach (var idIndex in candidates.Where(index => index >= 0 && index < tokens.Count).Distinct())
        {
            if (!TryParseCanId(tokens[idIndex], out var id, out var isExtended))
            {
                continue;
            }

            var dataSearchStart = Math.Max(directionIndex, idIndex) + 1;
            if (!TryFindLengthAndData(tokens, dataSearchStart, out var data))
            {
                continue;
            }

            var offsetMilliseconds = TryFindOffsetMilliseconds(tokens, directionIndex, out var parsedOffset)
                ? parsedOffset
                : 0;
            frame = CreateFrame(
                start, haveStart, offsetMilliseconds, defaultChannel, id, isExtended, direction, data);
            return true;
        }

        return false;
    }

    private static CanFrame CreateFrame(
        DateTimeOffset start,
        bool haveStart,
        double offsetMilliseconds,
        int channel,
        uint id,
        bool isExtended,
        CanDirection direction,
        byte[] data) => new()
    {
        Timestamp = (haveStart ? start : DateTimeOffset.UnixEpoch).AddMilliseconds(offsetMilliseconds),
        Channel = channel,
        Id = id,
        Data = data,
        Protocol = BusProtocol.ClassicalCan,
        Direction = direction,
        IsExtended = isExtended
    };

    private static bool TryReadKeyword(string line, string keyword, out string value)
    {
        var source = line[0] == ';' ? line[1..] : line;
        var prefix = keyword + "=";
        if (source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = source[prefix.Length..].Trim().TrimEnd(';').Trim();
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
        if (!token.Contains('.') && token.Contains(','))
        {
            token = token.Replace(',', '.');
        }

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

    private static bool TryFindDirection(
        IReadOnlyList<string> tokens,
        out int directionIndex,
        out CanDirection direction)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (TryParseDirection(tokens[index], out direction))
            {
                directionIndex = index;
                return true;
            }
        }

        directionIndex = -1;
        direction = default;
        return false;
    }

    private static bool TryFindLengthAndData(
        IReadOnlyList<string> tokens,
        int searchStart,
        out byte[] data)
    {
        var searchEnd = Math.Min(tokens.Count, searchStart + 3);
        for (var index = searchStart; index < searchEnd; index++)
        {
            if (!TryParseClassicalLength(tokens[index], out var length) ||
                !TryParseData(tokens, index + 1, length, out data))
            {
                continue;
            }

            return true;
        }

        data = [];
        return false;
    }

    private static bool TryFindOffsetMilliseconds(
        IReadOnlyList<string> tokens,
        int directionIndex,
        out double milliseconds)
    {
        var startIndex = LooksLikeMessageNumber(tokens[0]) ? 1 : 0;
        for (var index = startIndex; index < directionIndex; index++)
        {
            if (TryParseOffsetMilliseconds(tokens[index], out milliseconds))
            {
                return true;
            }
        }

        milliseconds = 0;
        return false;
    }

    private static bool LooksLikeMessageNumber(string token)
    {
        var value = token.Trim();
        if (value.EndsWith(')'))
        {
            value = value[..^1];
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryParseCanId(string token, out uint id, out bool isExtended)
    {
        var value = token.Trim();
        var explicitExtended = value.EndsWith('x') || value.EndsWith('X');
        value = value.TrimEnd('h', 'H', 'x', 'X');
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

        isExtended = explicitExtended || value.Length == 8 || id > 0x7FF;
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

    private static bool IsRemoteRecord(IEnumerable<string> tokens) => tokens.Any(token =>
        token.Equals("RTR", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("REMOTE", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("RR", StringComparison.OrdinalIgnoreCase));

    private static bool IsErrorStatusOrUnsupportedRecord(
        IReadOnlyList<string> tokens,
        string line,
        IReadOnlyList<string>? columns)
    {
        if (line.Contains("Error Frame", StringComparison.OrdinalIgnoreCase) ||
            tokens.Any(token => token.Equals("STATUS", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var typeIndex = columns is null ? -1 : IndexOfColumn(columns, "T");
        if (typeIndex < 0 || typeIndex >= tokens.Count)
        {
            return tokens.Any(token => token.Equals("ER", StringComparison.OrdinalIgnoreCase));
        }

        return tokens[typeIndex].ToUpperInvariant() is
            "ER" or "ST" or "EC" or "EV" or "PE" or "OF" or "EN" or
            "FD" or "FB" or "FE" or "BI" or "XL";
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
