using System.Globalization;
using System.Text;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Storage;

public static class IncidentComparisonReportCodec
{
    public static string BuildMarkdown(
        LoadedIncidentPackage baseline,
        LoadedIncidentPackage current,
        IncidentEventChainComparisonResult result,
        MachineProfile? profile = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(result);

        var generated = generatedAt ?? DateTimeOffset.UtcNow;
        var builder = new StringBuilder();

        builder.AppendLine("# CraneCAN — GOOD/FAULT Incident Chain Comparison");
        builder.AppendLine();
        builder.AppendLine(
            $"Сформировано UTC: {generated.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}");
        builder.AppendLine("Режим: offline / read-only; CAN Tx отсутствует.");
        builder.AppendLine("Интерпретация: отчёт фиксирует наблюдаемые различия CAN-цепочек и не доказывает физическую причину неисправности.");
        builder.AppendLine();

        builder.AppendLine("## Контекст");
        builder.AppendLine();
        builder.AppendLine("| Поле | ЭТАЛОН / GOOD / BEFORE | СРАВНЕНИЕ / FAULT / AFTER |");
        builder.AppendLine("|---|---|---|");
        AppendContextRow(builder, "Файл",
            Path.GetFileName(baseline.MetadataPath),
            Path.GetFileName(current.MetadataPath));
        AppendContextRow(builder, "Incident ID",
            baseline.Incident.IncidentId.ToString("N"),
            current.Incident.IncidentId.ToString("N"));
        AppendContextRow(builder, "Capture origin",
            baseline.CaptureOrigin,
            current.CaptureOrigin);
        AppendContextRow(builder, "Driver",
            baseline.Source.DriverId,
            current.Source.DriverId);
        AppendContextRow(builder, "CAN channel",
            baseline.Source.ChannelId,
            current.Source.ChannelId);
        AppendContextRow(builder, "Bitrate metadata",
            FormatBitrate(baseline.Source.Bitrate),
            FormatBitrate(current.Source.Bitrate));
        AppendContextRow(builder, "LISTEN ONLY confirmed",
            baseline.Source.ListenOnlyConfirmed ? "да" : "нет/неизвестно",
            current.Source.ListenOnlyConfirmed ? "да" : "нет/неизвестно");
        AppendContextRow(builder, "Полнота",
            baseline.Incident.Complete ? "полная" : "ограничена",
            current.Incident.Complete ? "полная" : "ограничена");
        AppendContextRow(builder, "Quality codes",
            JoinOrDash(baseline.Incident.QualityCodes),
            JoinOrDash(current.Incident.QualityCodes));
        AppendContextRow(builder, "CAN кадров",
            baseline.Incident.Frames.Count.ToString("N0", CultureInfo.InvariantCulture),
            current.Incident.Frames.Count.ToString("N0", CultureInfo.InvariantCulture));
        AppendContextRow(builder, "Markers",
            baseline.Incident.Markers.Count.ToString(CultureInfo.InvariantCulture),
            current.Incident.Markers.Count.ToString(CultureInfo.InvariantCulture));
        AppendContextRow(builder, "Окно incident",
            FormatDuration(baseline.Incident.WindowEnd - baseline.Incident.WindowStart),
            FormatDuration(current.Incident.WindowEnd - current.Incident.WindowStart));
        builder.AppendLine();

        builder.AppendLine("## Machine Profile");
        builder.AppendLine();
        if (profile is null)
        {
            builder.AppendLine("Machine Profile: не загружен.");
        }
        else
        {
            builder.AppendLine($"- Profile ID: `{profile.ProfileId:N}`");
            builder.AppendLine($"- Машина: {MarkdownText(profile.MachineName)}");
            builder.AppendLine($"- Производитель / модель: {MarkdownText(profile.Manufacturer)} / {MarkdownText(profile.Model)}");
            builder.AppendLine($"- CAN bus: {MarkdownText(profile.CanBusName)}");
            builder.AppendLine($"- Bitrate profile: {FormatBitrate(profile.Bitrate)}");
            builder.AppendLine($"- Known signals: {profile.KnownSignals.Count:N0}");
            builder.AppendLine($"- Experimental signals: {profile.ExperimentalSignals.Count:N0}");
        }
        builder.AppendLine();

        builder.AppendLine("## Итог сравнения");
        builder.AppendLine();
        builder.AppendLine($"- Шагов GOOD: {result.BaselineStepCount:N0}");
        builder.AppendLine($"- Шагов FAULT: {result.CurrentStepCount:N0}");
        builder.AppendLine($"- HIGH: {result.HighCount:N0}");
        builder.AppendLine($"- MEDIUM: {result.MediumCount:N0}");
        builder.AppendLine($"- INFO: {result.InfoCount:N0}");
        builder.AppendLine(
            $"- Порог timing: {result.TimingThresholdMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms");
        builder.AppendLine($"- Первое наблюдаемое различие: {(result.Earliest is null ? "не найдено" : FormatRelativeTime(result.Earliest.EvidenceMilliseconds))}");
        builder.AppendLine();

        builder.AppendLine("## Предупреждения качества");
        builder.AppendLine();
        if (result.Warnings.Count == 0)
        {
            builder.AppendLine("- Не сформированы.");
        }
        else
        {
            foreach (var warning in result.Warnings)
                builder.AppendLine($"- {MarkdownText(warning)}");
        }
        builder.AppendLine();

        builder.AppendLine("## Различия");
        builder.AppendLine();
        if (result.Differences.Count == 0)
        {
            builder.AppendLine("По текущим критериям различий не найдено.");
        }
        else
        {
            builder.AppendLine("| # | Приоритет | Различие | ID | Формат | Место | Событие | Profile signal | GOOD время | FAULT время | Δt | GOOD | FAULT | Наблюдение |");
            builder.AppendLine("|---:|---|---|---|---|---|---|---|---:|---:|---:|---|---|---|");

            for (var index = 0; index < result.Differences.Count; index++)
            {
                var item = result.Differences[index];
                builder.Append("| ").Append(index + 1).Append(" | ")
                    .Append(MarkdownCell(PriorityText(item.Priority))).Append(" | ")
                    .Append(MarkdownCell(DifferenceKindText(item.DifferenceKind))).Append(" | ")
                    .Append(MarkdownCell(FormatId(item.Id, item.IsExtended))).Append(" | ")
                    .Append(MarkdownCell(item.IsExtended ? "Extended" : "Standard")).Append(" | ")
                    .Append(MarkdownCell(item.DataIndex.HasValue ? $"DATA[{item.DataIndex.Value}]" : "ID/DLC")).Append(" | ")
                    .Append(MarkdownCell(StepKindText(item.StepKind))).Append(" | ")
                    .Append(MarkdownCell(JoinOrDash(item.ProfileSignals))).Append(" | ")
                    .Append(MarkdownCell(FormatNullableRelativeTime(item.BaselineReactionMilliseconds))).Append(" | ")
                    .Append(MarkdownCell(FormatNullableRelativeTime(item.CurrentReactionMilliseconds))).Append(" | ")
                    .Append(MarkdownCell(FormatDelta(item.TimingDeltaMilliseconds))).Append(" | ")
                    .Append(MarkdownCell(item.BaselineValue)).Append(" | ")
                    .Append(MarkdownCell(item.CurrentValue)).Append(" | ")
                    .Append(MarkdownCell(item.Description)).AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Ограничение");
        builder.AppendLine();
        builder.AppendLine(
            "Первое расхождение является местом расхождения двух наблюдаемых CAN-последовательностей по используемым критериям. " +
            "Оно не идентифицирует автоматически неисправный ECU, датчик, провод, клапан или механический узел.");
        builder.AppendLine(
            "Вывод рекомендуется подтверждать повторяемым экспериментом, Machine Profile evidence, схемой и независимыми электрическими/гидравлическими измерениями.");

        return builder.ToString();
    }

    public static async Task SaveAsync(
        string path,
        LoadedIncidentPackage baseline,
        LoadedIncidentPackage current,
        IncidentEventChainComparisonResult result,
        MachineProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var text = BuildMarkdown(baseline, current, result, profile);
        await File.WriteAllTextAsync(
            fullPath,
            text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static void AppendContextRow(
        StringBuilder builder,
        string field,
        string? baseline,
        string? current)
    {
        builder.Append("| ")
            .Append(MarkdownCell(field)).Append(" | ")
            .Append(MarkdownCell(baseline)).Append(" | ")
            .Append(MarkdownCell(current)).AppendLine(" |");
    }

    private static string PriorityText(IncidentEventChainDifferencePriority priority) => priority switch
    {
        IncidentEventChainDifferencePriority.High => "HIGH",
        IncidentEventChainDifferencePriority.Medium => "MEDIUM",
        _ => "INFO"
    };

    private static string DifferenceKindText(IncidentEventChainDifferenceKind kind) => kind switch
    {
        IncidentEventChainDifferenceKind.StepOnlyInBaseline => "только GOOD",
        IncidentEventChainDifferenceKind.StepOnlyInCurrent => "только FAULT",
        IncidentEventChainDifferenceKind.TransitionChanged => "переход изменился",
        IncidentEventChainDifferenceKind.TimingChanged => "тайминг изменился",
        IncidentEventChainDifferenceKind.BreakpointStateChanged => "признак разрыва",
        _ => kind.ToString()
    };

    private static string StepKindText(IncidentTransitionKind kind) => kind switch
    {
        IncidentTransitionKind.IdAppeared => "ID появился",
        IncidentTransitionKind.PeriodicIdStopped => "ID прекратился",
        IncidentTransitionKind.DlcChanged => "DLC изменился",
        IncidentTransitionKind.ByteChanged => "байт изменился",
        _ => kind.ToString()
    };

    private static string FormatId(uint id, bool extended) =>
        extended ? $"0x{id:X8}" : $"0x{id:X3}";

    private static string FormatBitrate(int? bitrate) =>
        bitrate.HasValue
            ? bitrate.Value.ToString("N0", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatDuration(TimeSpan duration) =>
        $"{duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    private static string FormatNullableRelativeTime(double? milliseconds) =>
        milliseconds.HasValue ? FormatRelativeTime(milliseconds.Value) : "—";

    private static string FormatRelativeTime(double milliseconds)
    {
        var seconds = milliseconds / 1000.0;
        var formatted = seconds.ToString("0.000", CultureInfo.InvariantCulture);
        return seconds >= 0 ? $"+{formatted} s" : $"{formatted} s";
    }

    private static string FormatDelta(double? milliseconds) =>
        milliseconds.HasValue
            ? $"{milliseconds.Value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture)} ms"
            : "—";

    private static string JoinOrDash(IEnumerable<string> values)
    {
        var array = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return array.Length == 0 ? "—" : string.Join("; ", array);
    }

    private static string MarkdownText(string? value) =>
        MarkdownCell(value);

    private static string MarkdownCell(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        return value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal);
    }
}
