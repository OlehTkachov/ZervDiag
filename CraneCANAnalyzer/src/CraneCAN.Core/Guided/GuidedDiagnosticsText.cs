namespace CraneCAN.Core.Guided;

public static class GuidedDiagnosticsText
{
    public static string Quality(ExperimentQualityIssue issue) => issue.Code switch
    {
        ExperimentQualityCode.Ready => "Эксперимент пригоден для анализа",
        ExperimentQualityCode.EmptyReference => "нет сравнимых кадров REFERENCE",
        ExperimentQualityCode.EmptyAction => "нет сравнимых кадров ACTION",
        ExperimentQualityCode.TooShortReference => "REFERENCE слишком короткий",
        ExperimentQualityCode.TooShortAction => "ACTION слишком короткий",
        ExperimentQualityCode.TooFewReferenceFrames => "в REFERENCE слишком мало кадров",
        ExperimentQualityCode.TooFewActionFrames => "в ACTION слишком мало кадров",
        ExperimentQualityCode.OverlappingWindows => "окна REFERENCE и ACTION пересекаются",
        ExperimentQualityCode.ReferenceAfterAction => "REFERENCE расположен после ACTION — проверьте, не перепутаны ли состояния",
        ExperimentQualityCode.DifferentBuses => "сравниваются разные CAN buses",
        ExperimentQualityCode.EventNotDetected => "чёткий момент действия не обнаружен",
        ExperimentQualityCode.EventBoundaryUnclear => "момент действия определён неоднозначно",
        ExperimentQualityCode.LowRepeatability => "кандидаты имеют низкую повторяемость",
        _ => issue.Code.ToString()
    };

    public static string Score(ScoreContribution contribution) => contribution.Reason switch
    {
        ScoreReason.RepeatedAllThreeOrMore => "повторяется во всех опытах (не менее 3)",
        ScoreReason.RepeatedAllAvailable => "повторяется во всех имеющихся опытах",
        ScoreReason.StableBitTransition => "устойчивый переход одного бита",
        ScoreReason.StableByteTransition => "устойчивое изменение байта",
        ScoreReason.MessagePresenceChange => "появление или исчезновение сообщения",
        ScoreReason.AnalogRamp => "направленное плавное изменение",
        ScoreReason.OccursAfterAction => "изменение возникает после действия",
        ScoreReason.ReturnsToBaseline => "возвращается к исходному состоянию",
        ScoreReason.AgreementAbove95 => "стабильность выше 95%",
        ScoreReason.ObservedInReference => "изменение наблюдается и в REFERENCE",
        ScoreReason.LowRepeatability => "низкая повторяемость",
        ScoreReason.AnalogNoise => "похоже на аналоговый шум",
        ScoreReason.OccursBeforeAction => "изменение начинается до действия",
        _ => contribution.Reason.ToString()
    };

    public static string Interpretation(GuidedCandidate candidate) =>
        candidate.Score >= 80
            ? $"Сильно коррелирует с «{candidate.ActionName}». Требуется подтверждение evidence."
            : candidate.Score >= 50
                ? $"Кандидат на сигнал «{candidate.ActionName}». Нужен повторный опыт."
                : "Слабый кандидат; возможен фоновый или аналоговый шум.";
}
