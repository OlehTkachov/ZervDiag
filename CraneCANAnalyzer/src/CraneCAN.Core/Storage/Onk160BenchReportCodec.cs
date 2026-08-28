using System.Globalization;
using System.Text;
using CraneCAN.Core.Analysis;

namespace CraneCAN.Core.Storage;

public sealed record Onk160BenchReportMetadata(
    DateTimeOffset CreatedAt,
    string TestName,
    string ElectricalAction,
    string BoiCode,
    string BoiText,
    int NormalCycles,
    int ChangedCycles);

public static class Onk160BenchReportCodec
{
    public static async Task SaveAsync(
        string path,
        Onk160BenchReportMetadata metadata,
        IEnumerable<Onk160BenchComparison> comparison,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));

        await WriteRowAsync(writer, cancellationToken, "Параметр", "Значение");
        await WriteRowAsync(writer, cancellationToken, "Дата UTC", metadata.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await WriteRowAsync(writer, cancellationToken, "Испытание", metadata.TestName);
        await WriteRowAsync(writer, cancellationToken, "Электрическое воздействие", metadata.ElectricalAction);
        await WriteRowAsync(writer, cancellationToken, "Код БОИ", metadata.BoiCode);
        await WriteRowAsync(writer, cancellationToken, "Текст БОИ", metadata.BoiText);
        await WriteRowAsync(writer, cancellationToken, "Циклов нормы", metadata.NormalCycles.ToString(CultureInfo.InvariantCulture));
        await WriteRowAsync(writer, cancellationToken, "Циклов изменения", metadata.ChangedCycles.ToString(CultureInfo.InvariantCulture));
        await writer.WriteLineAsync();
        await WriteRowAsync(writer, cancellationToken,
            "Пакет", "Байт", "Поле", "Норма HEX", "Изменение HEX", "XOR",
            "Изменённые биты", "Изменено", "Стабильность нормы, %",
            "Стабильность изменения, %", "Наличие нормы, %", "Наличие изменения, %");

        foreach (var row in comparison)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRowAsync(writer, cancellationToken,
                row.Header.ToString("X2"),
                row.DataIndex?.ToString(CultureInfo.InvariantCulture) ?? "—",
                row.Field,
                FormatByte(row.NormalValue),
                FormatByte(row.ChangedValue),
                FormatByte(row.XorMask),
                row.ChangedBits,
                row.IsChanged ? "да" : "нет",
                row.NormalAgreementPercent.ToString("F1", CultureInfo.InvariantCulture),
                row.ChangedAgreementPercent.ToString("F1", CultureInfo.InvariantCulture),
                row.NormalPresencePercent.ToString("F1", CultureInfo.InvariantCulture),
                row.ChangedPresencePercent.ToString("F1", CultureInfo.InvariantCulture));
        }
    }

    private static string FormatByte(byte? value) => value.HasValue ? value.Value.ToString("X2") : "—";

    private static async Task WriteRowAsync(
        TextWriter writer,
        CancellationToken cancellationToken,
        params string[] values)
    {
        var line = string.Join(";", values.Select(Escape));
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static string Escape(string value)
    {
        if (!value.Contains(';') && !value.Contains('"') && !value.Contains('\r') && !value.Contains('\n'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
