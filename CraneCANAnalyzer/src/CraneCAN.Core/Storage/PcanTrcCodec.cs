using System.Globalization;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Storage;

/// <summary>
/// Tolerant reader for classic PCAN-View text trace files (.trc).
/// It intentionally ignores comments, status/error lines and any record that
/// cannot be identified as a normal Rx/Tx data frame. Both 11-bit and 29-bit
/// identifiers are preserved.
/// </summary>
public static class PcanTrcCodec
{
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
        var start = DateTimeOffset.UnixEpoch;
        var haveStart = false;

        foreach (var sourceLine in lines)
        {
            var line = sourceLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(";$STARTTIME=", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(line.IndexOf('=') + 1)..].Trim();
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oaDate))
                {
                    try
                    {
                        var dt = DateTime.SpecifyKind(DateTime.FromOADate(oaDate), DateTimeKind.Local);
                        start = new DateTimeOffset(dt).ToUniversalTime();
                        haveStart = true;
                    }
                    catch (ArgumentException)
                    {
                    }
                }
                continue;
            }

            if (line[0] == ';' || line[0] == '$')
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!TryFindDirection(tokens, out var directionIndex, out var direction))
            {
                continue;
            }

            if (!TryFindId(tokens, directionIndex, out var idIndex, out var id))
            {
                continue;
            }

            if (!TryFindDlcAndData(tokens, directionIndex, idIndex, out var data))
            {
                continue;
            }

            var offsetMs = TryFindOffsetMilliseconds(tokens, directionIndex, out var parsedOffset)
                ? parsedOffset
                : result.Count;
            var timestamp = (haveStart ? start : DateTimeOffset.UnixEpoch).AddMilliseconds(offsetMs);
            var isExtended = id > 0x7FF;

            var frame = new CanFrame
            {
                Timestamp = timestamp,
                Channel = defaultChannel,
                Id = id,
                Data = data,
                Protocol = BusProtocol.ClassicalCan,
                Direction = direction,
                IsExtended = isExtended
            };
            frame.Validate();
            result.Add(frame);
        }

        if (result.Count == 0)
        {
            throw new FormatException("В TRC не найдено ни одного обычного CAN-кадра Rx/Tx.");
        }

        return result;
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

    private static bool TryFindId(
        IReadOnlyList<string> tokens,
        int directionIndex,
        out int idIndex,
        out uint id)
    {
        // Common PCAN-View 1.x layout: number, time, Rx/Tx, ID, DLC, data...
        for (var index = directionIndex + 1; index < Math.Min(tokens.Count, directionIndex + 4); index++)
        {
            if (TryParseCanId(tokens[index], out id))
            {
                idIndex = index;
                return true;
            }
        }

        // Some trace layouts put ID before Rx/Tx.
        for (var index = directionIndex - 1; index >= Math.Max(0, directionIndex - 4); index--)
        {
            if (TryParseCanId(tokens[index], out id))
            {
                idIndex = index;
                return true;
            }
        }

        idIndex = -1;
        id = 0;
        return false;
    }

    private static bool TryParseCanId(string token, out uint id)
    {
        var value = token.Trim().TrimEnd('h', 'H');
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length is < 2 or > 8 || !value.All(Uri.IsHexDigit))
        {
            id = 0;
            return false;
        }

        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
        {
            return false;
        }

        return id <= 0x1FFFFFFF;
    }

    private static bool TryFindDlcAndData(
        IReadOnlyList<string> tokens,
        int directionIndex,
        int idIndex,
        out byte[] data)
    {
        var startIndex = Math.Max(directionIndex, idIndex) + 1;
        for (var index = startIndex; index < tokens.Count; index++)
        {
            if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dlc) ||
                dlc is < 0 or > 8)
            {
                continue;
            }

            if (tokens.Count - index - 1 < dlc)
            {
                continue;
            }

            var bytes = new byte[dlc];
            var valid = true;
            for (var byteIndex = 0; byteIndex < dlc; byteIndex++)
            {
                var token = tokens[index + 1 + byteIndex].Trim().TrimEnd('h', 'H');
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    token = token[2..];
                }

                if (token.Length > 2 ||
                    !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[byteIndex]))
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

    private static bool TryFindOffsetMilliseconds(
        IReadOnlyList<string> tokens,
        int directionIndex,
        out double milliseconds)
    {
        for (var index = 0; index < directionIndex; index++)
        {
            var token = tokens[index].TrimEnd(')', ':');
            if (token.Contains('.') &&
                double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds) &&
                milliseconds >= 0)
            {
                return true;
            }
        }

        milliseconds = 0;
        return false;
    }
}
