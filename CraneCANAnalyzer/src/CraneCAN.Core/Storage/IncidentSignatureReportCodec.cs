using System.Globalization;
using System.Text;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Storage;

public static class IncidentSignatureReportCodec
{
    public static string BuildMarkdown(
        IReadOnlyList<LoadedIncidentPackage> packages,
        IncidentSignatureAnalysisResult result,
        MachineProfile? profile = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(result);

        Validate(packages, result);

        var generated = generatedAt ?? DateTimeOffset.UtcNow;
        var builder = new StringBuilder();

        builder.AppendLine("# CraneCAN — Cross-Incident Signature Report");
        builder.AppendLine();
        builder.AppendLine(
            $"Сформировано UTC: {generated.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}");
        builder.AppendLine("Режим: offline / read-only; CAN Tx отсутствует.");
        builder.AppendLine(
            "Интерпретация: повторяемость CAN-наблюдения не доказывает физическую причинность, " +
            "не идентифицирует автоматически неисправный ECU и не заменяет независимую проверку.");
        builder.AppendLine();

        builder.AppendLine("## Серия incident");
        builder.AppendLine();
        builder.AppendLine(
            "| # | Файл | Incident ID | Capture origin | Driver | CAN channel | Bitrate | LISTEN ONLY | Полнота | Quality codes | CAN кадров | Marker | Окно |");
        builder.AppendLine(
            "|---:|---|---|---|---|---|---:|---|---|---|---:|---|---:|");

        for (var index = 0; index < packages.Count; index++)
        {
            var package = packages[index];
            var marker = package.Incident.Markers
                .OrderBy(item => item.Timestamp)
                .First();

            builder.Append("| ").Append(index + 1).Append(" | ")
                .Append(MarkdownCell(Path.GetFileName(package.MetadataPath))).Append(" | ")
                .Append(MarkdownCell(package.Incident.IncidentId.ToString("N"))).Append(" | ")
                .Append(MarkdownCell(package.CaptureOrigin)).Append(" | ")
                .Append(MarkdownCell(package.Source.DriverId)).Append(" | ")
                .Append(MarkdownCell(package.Source.ChannelId)).Append(" | ")
                .Append(MarkdownCell(FormatBitrate(package.Source.Bitrate))).Append(" | ")
                .Append(MarkdownCell(package.Source.ListenOnlyConfirmed ? "да" : "нет/неизвестно")).Append(" | ")
                .Append(MarkdownCell(package.Incident.Complete ? "полная" : "ограничена")).Append(" | ")
                .Append(MarkdownCell(JoinOrDash(package.Incident.QualityCodes))).Append(" | ")
                .Append(package.Incident.Frames.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(MarkdownCell(marker.Label)).Append(" | ")
                .Append(MarkdownCell(FormatDuration(package.Incident.WindowEnd - package.Incident.WindowStart)))
                .AppendLine(" |");
        }

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
            builder.AppendLine(
                $"- Производитель / модель: {MarkdownText(profile.Manufacturer)} / {MarkdownText(profile.Model)}");
            builder.AppendLine($"- CAN bus: {MarkdownText(profile.CanBusName)}");
            builder.AppendLine($"- Bitrate profile: {FormatBitrate(profile.Bitrate)}");
            builder.AppendLine($"- Known signals: {profile.KnownSignals.Count.ToString("N0", CultureInfo.InvariantCulture)}");
            builder.AppendLine(
                $"- Experimental signals: {profile.ExperimentalSignals.Count.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine();
        builder.AppendLine("## Итог");
        builder.AppendLine();
        builder.AppendLine($"- Incident: {result.IncidentCount.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- HIGH: {result.HighCount.ToString("N0", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- MEDIUM: {result.MediumCount.ToString("N0", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- INFO: {result.InfoCount.ToString("N0", CultureInfo.InvariantCulture)}");
        builder.AppendLine(
            result.EarliestHigh is null
                ? "- Самый ранний HIGH: не найден."
                : "- Самый ранний HIGH: " +
                  $"{FormatRelativeTime(result.EarliestHigh.MedianReactionMilliseconds)} · " +
                  $"{FormatId(result.EarliestHigh.Id, result.EarliestHigh.IsExtended)} · " +
                  $"{LocationText(result.EarliestHigh.DataIndex)}.");

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
        builder.AppendLine("## Кандидаты повторяемости");
        builder.AppendLine();

        if (result.Candidates.Count == 0)
        {
            builder.AppendLine("Кандидаты отсутствуют.");
        }
        else
        {
            builder.AppendLine(
                "| # | Приоритет | ID | Формат | Место | Событие | Profile signal | Повтор | Согласие перехода | Median t | Min t | Max t | Разброс t | Разрыв | Модальный переход | Наблюдение |");
            builder.AppendLine(
                "|---:|---|---|---|---|---|---|---|---|---:|---:|---:|---:|---|---|---|");

            for (var index = 0; index < result.Candidates.Count; index++)
            {
                var candidate = result.Candidates[index];
                builder.Append("| ").Append(index + 1).Append(" | ")
                    .Append(MarkdownCell(PriorityText(candidate.Priority))).Append(" | ")
                    .Append(MarkdownCell(FormatId(candidate.Id, candidate.IsExtended))).Append(" | ")
                    .Append(MarkdownCell(candidate.IsExtended ? "Extended" : "Standard")).Append(" | ")
                    .Append(MarkdownCell(LocationText(candidate.DataIndex))).Append(" | ")
                    .Append(MarkdownCell(KindText(candidate.Kind))).Append(" | ")
                    .Append(MarkdownCell(JoinOrDash(candidate.ProfileSignals))).Append(" | ")
                    .Append(MarkdownCell(
                        $"{candidate.OccurrenceCount}/{candidate.IncidentCount} " +
                        $"({candidate.RepeatabilityPercent.ToString("0.#", CultureInfo.InvariantCulture)}%)"))
                    .Append(" | ")
                    .Append(MarkdownCell(
                        $"{candidate.TransitionAgreementCount}/{candidate.OccurrenceCount} " +
                        $"({candidate.TransitionAgreementPercent.ToString("0.#", CultureInfo.InvariantCulture)}%)"))
                    .Append(" | ")
                    .Append(MarkdownCell(FormatRelativeTime(candidate.MedianReactionMilliseconds))).Append(" | ")
                    .Append(MarkdownCell(FormatRelativeTime(candidate.MinimumReactionMilliseconds))).Append(" | ")
                    .Append(MarkdownCell(FormatRelativeTime(candidate.MaximumReactionMilliseconds))).Append(" | ")
                    .Append(MarkdownCell(FormatMilliseconds(candidate.TimingSpreadMilliseconds))).Append(" | ")
                    .Append(MarkdownCell(
                        $"{candidate.BreakpointCount}/{candidate.OccurrenceCount}"))
                    .Append(" | ")
                    .Append(MarkdownCell(
                        $"{candidate.ModalBaselineValue} → {candidate.ModalObservedValue}"))
                    .Append(" | ")
                    .Append(MarkdownCell(candidate.Description))
                    .AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Ограничение интерпретации");
        builder.AppendLine();
        builder.AppendLine(
            "HIGH означает устойчиво повторяющийся наблюдаемый CAN-шаг по текущим критериям CraneCAN. " +
            "Это не доказательство того, что найденный ID/бит является первичной причиной неисправности, " +
            "и не доказывает назначение ECU, датчика, клапана, реле или механического узла.");
        builder.AppendLine(
            "Для инженерного подтверждения сопоставьте результат с GOOD/FAULT comparison, Machine Profile evidence, " +
            "электрической/гидравлической схемой, документацией, измерением и физической реакцией машины.");

        return builder.ToString();
    }

    public static async Task SaveAsync(
        string path,
        IReadOnlyList<LoadedIncidentPackage> packages,
        IncidentSignatureAnalysisResult result,
        MachineProfile? profile = null,
        DateTimeOffset? generatedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var text = BuildMarkdown(packages, result, profile, generatedAt);
        await File.WriteAllTextAsync(
            fullPath,
            text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(
        IReadOnlyList<LoadedIncidentPackage> packages,
        IncidentSignatureAnalysisResult result)
    {
        if (packages.Count != result.IncidentCount)
        {
            throw new ArgumentException(
                $"Количество incident packages ({packages.Count}) не совпадает с результатом анализа ({result.IncidentCount}).",
                nameof(packages));
        }

        var duplicate = packages
            .GroupBy(package => package.Incident.IncidentId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Серия содержит повторный Incident ID {duplicate.Key:N}. Копии одной записи нельзя считать независимыми.",
                nameof(packages));
        }

        for (var index = 0; index < packages.Count; index++)
        {
            if (packages[index].Incident.Markers.Count == 0)
            {
                throw new ArgumentException(
                    $"Incident #{index + 1} не содержит marker.",
                    nameof(packages));
            }
        }

        if (result.Candidates.Any(candidate =>
                candidate.IncidentCount != result.IncidentCount))
        {
            throw new ArgumentException(
                "Кандидаты сигнатуры рассчитаны для другого количества incident.",
                nameof(result));
        }
    }

    private static string PriorityText(IncidentSignaturePriority priority) =>
        priority switch
        {
            IncidentSignaturePriority.High => "HIGH",
            IncidentSignaturePriority.Medium => "MEDIUM",
            _ => "INFO"
        };

    private static string KindText(IncidentTransitionKind kind) =>
        kind switch
        {
            IncidentTransitionKind.IdAppeared => "ID появился",
            IncidentTransitionKind.PeriodicIdStopped => "ID прекратился",
            IncidentTransitionKind.DlcChanged => "DLC изменился",
            IncidentTransitionKind.ByteChanged => "байт изменился",
            _ => kind.ToString()
        };

    private static string FormatId(uint id, bool extended) =>
        extended ? $"0x{id:X8}" : $"0x{id:X3}";

    private static string LocationText(int? dataIndex) =>
        dataIndex.HasValue ? $"DATA[{dataIndex.Value}]" : "ID/DLC";

    private static string FormatBitrate(int? bitrate) =>
        bitrate.HasValue
            ? bitrate.GetValueOrDefault().ToString("N0", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatDuration(TimeSpan duration) =>
        $"{duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    private static string FormatRelativeTime(double milliseconds)
    {
        var seconds = milliseconds / 1000.0;
        var formatted = seconds.ToString("0.000", CultureInfo.InvariantCulture);
        return seconds >= 0 ? $"+{formatted} s" : $"{formatted} s";
    }

    private static string FormatMilliseconds(double milliseconds) =>
        $"{milliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";

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
        if (string.IsNullOrWhiteSpace(value))
            return "—";

        return value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace("\r", "<br>", StringComparison.Ordinal);
    }
}
