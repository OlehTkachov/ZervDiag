using System.Globalization;
using System.Text;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Storage;

public static class CanCsvCodec
{
    private const string Header = "TimestampUtc,Channel,Direction,Protocol,Id,Extended,Remote,Error,ChecksumValid,Data";

    public static async Task SaveAsync(
        string path,
        IEnumerable<CanFrame> frames,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync(Header.AsMemory(), cancellationToken).ConfigureAwait(false);

        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame.Validate();
            var line = string.Join(",",
                frame.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                frame.Channel.ToString(CultureInfo.InvariantCulture),
                frame.Direction,
                frame.Protocol,
                frame.Id.ToString("X", CultureInfo.InvariantCulture),
                frame.IsExtended.ToString(CultureInfo.InvariantCulture),
                frame.IsRemote.ToString(CultureInfo.InvariantCulture),
                frame.IsError.ToString(CultureInfo.InvariantCulture),
                frame.IsChecksumValid.HasValue ? frame.IsChecksumValid.Value.ToString() : "",
                frame.DataText);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<IReadOnlyList<CanFrame>> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CanFrame>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(firstLine, Header, StringComparison.Ordinal))
        {
            throw new FormatException("Файл не является журналом CraneCAN Analyzer.");
        }

        string? line;
        var lineNumber = 1;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var columns = line.Split(',', 10);
            if (columns.Length != 10)
            {
                throw new FormatException($"Invalid CSV row {lineNumber}.");
            }

            var data = string.IsNullOrWhiteSpace(columns[9])
                ? []
                : columns[9].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                    .ToArray();

            bool? checksumValid = string.IsNullOrWhiteSpace(columns[8])
                ? null
                : bool.Parse(columns[8]);

            var frame = new CanFrame
            {
                Timestamp = DateTimeOffset.Parse(columns[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Channel = int.Parse(columns[1], CultureInfo.InvariantCulture),
                Direction = Enum.Parse<CanDirection>(columns[2], true),
                Protocol = Enum.Parse<BusProtocol>(columns[3], true),
                Id = uint.Parse(columns[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                IsExtended = bool.Parse(columns[5]),
                IsRemote = bool.Parse(columns[6]),
                IsError = bool.Parse(columns[7]),
                IsChecksumValid = checksumValid,
                Data = data
            };
            frame.Validate();
            result.Add(frame);
        }

        return result;
    }
}
