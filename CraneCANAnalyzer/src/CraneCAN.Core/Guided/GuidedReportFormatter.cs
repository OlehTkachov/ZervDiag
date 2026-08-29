using System.Globalization;
using System.Text;

namespace CraneCAN.Core.Guided;

public static class GuidedReportFormatter
{
    public static string Format(GuidedAnalysisResult result, string actionName, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        var text = new StringBuilder();
        text.AppendLine("CraneCAN 0.6 — GUIDED DIAGNOSTICS REPORT");
        text.AppendLine($"Создан: {createdAt:O}");
        text.AppendLine($"Эксперимент: {actionName}");
        text.AppendLine($"Качество: {(result.Quality.CanAnalyze ? "пригоден для анализа" : "непригоден")}");
        foreach (var issue in result.Quality.Issues)
        {
            text.AppendLine($"- {issue.Severity}: {GuidedDiagnosticsText.Quality(issue)}");
        }

        text.AppendLine();
        text.AppendLine($"Найдено кандидатов: {result.Candidates.Count}");
        text.AppendLine($"Кандидатов score ≥ 80: {result.Candidates.Count(candidate => candidate.Score >= 80)}");
        text.AppendLine();

        for (var index = 0; index < result.Candidates.Count; index++)
        {
            var candidate = result.Candidates[index];
            text.AppendLine($"КАНДИДАТ #{index + 1}");
            text.AppendLine($"Статус: {candidate.Status.ToString().ToUpperInvariant()}");
            text.AppendLine($"ID: 0x{(candidate.IsExtended ? candidate.Id.ToString("X8") : candidate.Id.ToString("X3"))} ({(candidate.IsExtended ? "Extended" : "Standard")})");
            text.AppendLine($"Место: {Location(candidate)}");
            text.AppendLine($"Переход: {Byte(candidate.ReferenceValue)} → {Byte(candidate.ActionValue)}");
            text.AppendLine($"Реакция: {(candidate.ReactionMilliseconds.HasValue ? candidate.ReactionMilliseconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + " ms" : "не определена")}");
            text.AppendLine($"Повторяемость: {candidate.RepeatabilityCount}/{candidate.RepeatCount}");
            text.AppendLine($"Agreement: {candidate.AgreementPercent.ToString("0.0", CultureInfo.InvariantCulture)}%");
            text.AppendLine($"Score: {candidate.Score}/100");
            foreach (var contribution in candidate.ScoreExplanation)
            {
                text.AppendLine($"  {(contribution.Points >= 0 ? "+" : string.Empty)}{contribution.Points}: {GuidedDiagnosticsText.Score(contribution)}");
            }

            text.AppendLine($"Интерпретация: {GuidedDiagnosticsText.Interpretation(candidate)}");
            text.AppendLine();
        }

        text.AppendLine("ОГРАНИЧЕНИЕ ВЫВОДА");
        text.AppendLine("Отчёт описывает наблюдаемую CAN-корреляцию и не доказывает внутреннюю логику ECU.");
        text.AppendLine("CONFIRMED устанавливается пользователем только после добавления evidence.");
        text.AppendLine("CraneCAN не рекомендует обход защит или прямую подачу питания на исполнительные устройства.");
        return text.ToString();
    }

    private static string Location(GuidedCandidate candidate) => candidate.DataIndex.HasValue
        ? candidate.BitIndex.HasValue
            ? $"DATA[{candidate.DataIndex}], bit {candidate.BitIndex}"
            : $"DATA[{candidate.DataIndex}]"
        : "сообщение целиком";

    private static string Byte(byte? value) => value.HasValue ? $"0x{value.Value:X2}" : "—";
}
