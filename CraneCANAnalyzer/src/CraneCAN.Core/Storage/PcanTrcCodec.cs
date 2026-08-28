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
/// Tolerant, order-preserving reader for PCAN-View Classical CAN trace files.
/// PCAN trace headers, comments, RTR frames, error/status records and unknown
/// lines are counted and skipped without terminating the import.
/// </summary>
public static class PcanTrcCodec
{
    public static async Task<IReadOnlyList<CanFrame>> LoadAsync(
        string path,
        int defaultChannel = 0,
        CancellationToken cancellationToken = default) =>
        (await LoadWithDiagnosticsAsync(path, defaultChannel, cancellationToken).ConfigureAwait(false)).Frames;

    public static async Task<PcanTrcImportResult> LoadWithDiagnosticsAsync(
        string path,
        int defaultChannel = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateChannel(defaultChannel);
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
        var parser = new ParserState(defaultChannel);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parser.Process(line);
        }

        return parser.Complete();
    }

    public static IReadOnlyList<CanFrame> Parse(IEnumerable<string> lines, int defaultChannel = 0) =>
        ParseWithDiagnostics(lines, defaultChannel).Frames;

    public static PcanTrcImportResult ParseWithDiagnostics(
        IEnumerable<string> lines,
        int defaultChannel = 0)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ValidateChannel(defaultChannel);
        var parser = new ParserState(defaultChannel);
        foreach (var line in lines)
        {
            parser.Process(line);
        }

        return parser.Complete();
    }

    private static void ValidateChannel(int channel)
    {
        if (channel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }

    private sealed class ParserState
    {
        private readonly int _defaultChannel;
        private readonly List<CanFrame> _frames = [];
        private DateTimeOffset _start = DateTimeOffset.UnixEpoch;
        private bool _haveStart;

        public ParserState(int defaultChannel) => _defaultChannel = defaultChannel;

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

            if (line.StartsWith(";$STARTTIME=", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("$STARTTIME=", StringComparison.OrdinalIgnoreCase))
            {
                TrySetStartTime(line);
                HeaderAndCommentLines++;
                return;
            }

            if (line[0] is ';' or '$')
            {
                HeaderAndCommentLines++;
                return;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (IsErrorOrStatusRecord(tokens, line))
            {
                ErrorFramesSkipped++;
                return;
            }

            if (IsRemoteRecord(tokens))
            {
                RemoteFramesSkipped++;
                return;
            }

            if (!TryFindDirection(tokens, out var directionIndex, out var direction) ||
                !TryFindIdDlcAndData(tokens, directionIndex, out var id, out var isExtended, out var data))
            {
                UnknownOrMalformedLines++;
                return;
            }

            var offsetMs = TryFindOffsetMilliseconds(tokens, directionIndex, out var parsedOffset)
                ? parsedOffset
                : _frames.Count;
            var timestamp = (_haveStart ? _start : DateTimeOffset.UnixEpoch).AddMilliseconds(offsetMs);
            var frame = new CanFrame
            {
                Timestamp = timestamp,
                Channel = _defaultChannel,
                Id = id,
                Data = data,
                Protocol = BusProtocol.ClassicalCan,
                Direction = direction,
                IsExtended = isExtended
            };

            try
            {
                frame.Validate();
                _frames.Add(frame);
            }
            catch (ArgumentException)
            {
                UnknownOrMalformedLines++;
            }
        }

        public PcanTrcImportResult Complete()
        {
            if (_frames.Count == 0)
            {
                throw new FormatException(
                    "TRC-файл не содержит обычных кадров Classical CAN с данными. " +
                    "Комментарии, RTR, error/status и повреждённые строки были пропущены.");
            }

            return new PcanTrcImportResult(
                _frames,
                TotalLines,
                HeaderAndCommentLines,
                RemoteFramesSkipped,
                ErrorFramesSkipped,
                UnknownOrMalformedLines);
        }

        private void TrySetStartTime(string line)
        {
            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                return;
            }

            var value = line[(separator + 1)..].Trim();
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oaDate))
            {
                return;
            }

            try
            {
                var local = DateTime.SpecifyKind(DateTime.FromOADate(oaDate), DateTimeKind.Local);
                _start = new DateTimeOffset(local).ToUniversalTime();
                _haveStart = true;
            }
            catch (ArgumentException)
            {
                // Invalid metadata must not discard otherwise valid CAN frames.
            }
        }
    }

    private static bool TryFindDirection(
        IReadOnlyList<string> tokens,
        out int directionIndex,
        out CanDirection direction)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Equals("Rx", StringComparison.OrdinalIgnoreCase))
            {
                directionIndex = index;
                direction = CanDirection.Rx;
                return true;
            }

            if (tokens[index].Equals("Tx", StringComparison.OrdinalIgnoreCase))
            {
                directionIndex = index;
                direction = CanDirection.Tx;
                return true;
            }
        }

        directionIndex = -1;
        direction = default;
        return false;
    }

    private static bool TryFindIdDlcAndData(
        IReadOnlyList<string> tokens,
        int directionIndex,
        out uint id,
        out bool isExtended,
        out byte[] data)
    {
        // PCAN 1.x: number, offset, Rx/Tx, ID, DLC, DATA.
        // PCAN 2.x: number, offset, type, bus, ID, Rx/Tx, [reserved], DLC, DATA.
        // Checking the adjacent fields first prevents DATA[0] from being mistaken for an ID.
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
            if (!TryParseCanId(tokens[idIndex], out var candidateId, out var explicitExtended))
            {
                continue;
            }

            var dlcSearchStart = Math.Max(directionIndex, idIndex) + 1;
            if (!TryFindDlcAndData(tokens, dlcSearchStart, out var candidateData))
            {
                continue;
            }

            id = candidateId;
            isExtended = explicitExtended || candidateId > 0x7FF;
            data = candidateData;
            return true;
        }

        id = 0;
        isExtended = false;
        data = [];
        return false;
    }

    private static bool TryParseCanId(string token, out uint id, out bool explicitExtended)
    {
        var value = token.Trim();
        explicitExtended = value.EndsWith('x') || value.EndsWith('X');
        value = value.TrimEnd('h', 'H', 'x', 'X');
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length is < 2 or > 8 || !value.All(Uri.IsHexDigit) ||
            !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
        {
            id = 0;
            return false;
        }

        return id <= 0x1FFFFFFF;
    }

    private static bool TryFindDlcAndData(
        IReadOnlyList<string> tokens,
        int searchStart,
        out byte[] data)
    {
        var searchEnd = Math.Min(tokens.Count, searchStart + 3);
        for (var index = searchStart; index < searchEnd; index++)
        {
            if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dlc) ||
                dlc is < 0 or > 8 ||
                tokens.Count - index - 1 < dlc)
            {
                continue;
            }

            var bytes = new byte[dlc];
            var valid = true;
            for (var byteIndex = 0; byteIndex < dlc; byteIndex++)
            {
                if (!TryParseByte(tokens[index + 1 + byteIndex], out bytes[byteIndex]))
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                data = bytes;
                return true;
            }
        }

        data = [];
        return false;
    }

    private static bool TryParseByte(string source, out byte value)
    {
        var token = source.Trim().TrimEnd('h', 'H');
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            token = token[2..];
        }

        value = 0;
        return token.Length is 1 or 2 &&
               byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryFindOffsetMilliseconds(
        IReadOnlyList<string> tokens,
        int directionIndex,
        out double milliseconds)
    {
        var startIndex = LooksLikeMessageNumber(tokens[0]) ? 1 : 0;
        for (var index = startIndex; index < directionIndex; index++)
        {
            var token = tokens[index].Trim().TrimEnd(')', ':');
            if (token.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                token = token[..^2];
            }

            if (!token.Contains('.') && token.Contains(','))
            {
                token = token.Replace(',', '.');
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds) &&
                milliseconds >= 0)
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
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsRemoteRecord(IEnumerable<string> tokens) => tokens.Any(token =>
        token.Equals("RTR", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("REMOTE", StringComparison.OrdinalIgnoreCase));

    private static bool IsErrorOrStatusRecord(IEnumerable<string> tokens, string line) =>
        line.Contains("Error Frame", StringComparison.OrdinalIgnoreCase) ||
        tokens.Any(token =>
            token.Equals("ER", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("STATUS", StringComparison.OrdinalIgnoreCase));
}
