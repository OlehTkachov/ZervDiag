using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Profiles;

public sealed record MachineSignalEvidenceGroup(
    EvidenceKind Kind,
    string Title,
    int Count,
    string Interpretation);

public sealed record MachineSignalConfidenceExplanation(
    Guid SignalId,
    string SignalName,
    SignalKnowledgeState CurrentStatus,
    string StatusBasis,
    int EvidenceCount,
    bool HasExplicitConfirmation,
    bool HasIndependentEvidence,
    bool HasCrossIncidentEvidence,
    bool ReplayOnly,
    IReadOnlyList<MachineSignalEvidenceGroup> EvidenceGroups,
    IReadOnlyList<string> Cautions,
    IReadOnlyList<string> NextSteps);

/// <summary>
/// Explains the confidence already stored in Machine Profile without recalculating,
/// promoting or downgrading it. The result deliberately separates observed CAN
/// repeatability from independent documentation/measurement evidence.
/// </summary>
public static class MachineSignalConfidenceExplainer
{
    public static MachineSignalConfidenceExplanation Explain(MachineSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var evidence = signal.Evidence is null
            ? new List<SignalEvidence>()
            : signal.Evidence;

        var hasExplicitConfirmation = evidence.Any(item =>
            item.Kind == EvidenceKind.UserConfirmation);
        var hasIndependentEvidence = evidence.Any(item =>
            IsIndependentEvidence(item.Kind));
        var hasCrossIncidentEvidence = evidence.Any(item =>
            item.Kind == EvidenceKind.CrossIncidentSignature);
        var replayOnly = evidence.Count > 0 && evidence.All(IsReplayEvidence);

        var groups = evidence
            .GroupBy(item => item.Kind)
            .OrderBy(group => EvidenceOrder(group.Key))
            .ThenBy(group => group.Key)
            .Select(group => new MachineSignalEvidenceGroup(
                group.Key,
                EvidenceTitle(group.Key),
                group.Count(),
                EvidenceInterpretation(group.Key)))
            .ToArray();

        var cautions = BuildCautions(
            signal,
            evidence,
            hasExplicitConfirmation,
            hasIndependentEvidence,
            hasCrossIncidentEvidence,
            replayOnly);

        var nextSteps = BuildNextSteps(
            signal.Confidence,
            hasExplicitConfirmation,
            hasIndependentEvidence,
            hasCrossIncidentEvidence);

        return new MachineSignalConfidenceExplanation(
            signal.SignalId,
            signal.Name,
            signal.Confidence,
            BuildStatusBasis(
                signal.Confidence,
                hasExplicitConfirmation,
                hasCrossIncidentEvidence,
                hasIndependentEvidence),
            evidence.Count,
            hasExplicitConfirmation,
            hasIndependentEvidence,
            hasCrossIncidentEvidence,
            replayOnly,
            groups,
            cautions,
            nextSteps);
    }

    private static IReadOnlyList<string> BuildCautions(
        MachineSignal signal,
        IReadOnlyList<SignalEvidence> evidence,
        bool hasExplicitConfirmation,
        bool hasIndependentEvidence,
        bool hasCrossIncidentEvidence,
        bool replayOnly)
    {
        var cautions = new List<string>();

        if (evidence.Count == 0)
        {
            cautions.Add(
                "У сигнала нет сохранённых evidence. Статус существует без трассируемого обоснования в профиле.");
        }

        if (replayOnly)
        {
            cautions.Add(
                "Все сохранённые evidence получены только из Replay. Replay подтверждает работу анализа на записи, но не является независимым физическим опытом на машине.");
        }

        if (hasCrossIncidentEvidence && !hasIndependentEvidence)
        {
            cautions.Add(
                "Cross-Incident Signature подтверждает повторяемость наблюдаемого CAN-шагa, но независимая схема/документация/измерение ещё не приложены.");
        }
        else if (!hasIndependentEvidence && evidence.Count > 0)
        {
            cautions.Add(
                "Независимых evidence из схемы, документации, конфигурации или физического/электрического измерения нет.");
        }

        if (signal.Confidence == SignalKnowledgeState.Confirmed &&
            !hasExplicitConfirmation)
        {
            cautions.Add(
                "Статус CONFIRMED сохранён без evidence типа UserConfirmation. Это может быть импортированный/старый профиль; основание CONFIRMED нужно проверить вручную.");
        }

        var duplicateSourceReference = evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceReference))
            .GroupBy(
                item => $"{(int)item.Kind}:{item.SourceReference}",
                StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        if (duplicateSourceReference)
        {
            cautions.Add(
                "В evidence есть повторяющиеся Kind + SourceReference. Дубликаты не должны считаться независимыми подтверждениями.");
        }

        return cautions;
    }

    private static IReadOnlyList<string> BuildNextSteps(
        SignalKnowledgeState status,
        bool hasExplicitConfirmation,
        bool hasIndependentEvidence,
        bool hasCrossIncidentEvidence)
    {
        var steps = new List<string>();

        switch (status)
        {
            case SignalKnowledgeState.Unknown:
                steps.Add(
                    "Сначала сформируйте наблюдаемый кандидат через REFERENCE/ACTION или incident-анализ и сохраните источник evidence.");
                break;

            case SignalKnowledgeState.Candidate:
                if (!hasCrossIncidentEvidence)
                {
                    steps.Add(
                        "Повторите одно и то же физическое действие минимум в 3 независимых incident и проверьте Cross-Incident Repeatability / Signature.");
                }
                if (!hasIndependentEvidence)
                {
                    steps.Add(
                        "Подтвердите назначение сигнала независимым источником: схема, документация, конфигурация, мультиметр/ток или физический выход.");
                }
                steps.Add(
                    "Не переводите сигнал в CONFIRMED только по CAN-корреляции; финальное подтверждение должно быть явным инженерным решением.");
                break;

            case SignalKnowledgeState.Probable:
                if (!hasIndependentEvidence)
                {
                    steps.Add(
                        "Для CONFIRMED добавьте независимую проверку вне самой CAN-корреляции: схему, документацию или физическое/электрическое измерение.");
                }
                if (!hasExplicitConfirmation)
                {
                    steps.Add(
                        "После независимой проверки явно подтвердите назначение сигнала; это окно само не повышает статус до CONFIRMED.");
                }
                break;

            case SignalKnowledgeState.Confirmed:
                if (!hasExplicitConfirmation)
                {
                    steps.Add(
                        "Проверьте происхождение CONFIRMED и добавьте трассируемое UserConfirmation либо скорректируйте статус вручную.");
                }
                steps.Add(
                    "Сохраняйте ссылки на исходные трассы/документы и повторно проверяйте сигнал после изменения конфигурации машины или CAN-профиля.");
                break;

            case SignalKnowledgeState.Rejected:
                steps.Add(
                    "Не используйте отклонённый сигнал как подтверждённый. Причина REJECTED должна быть пересмотрена отдельно; новые evidence не восстанавливают его автоматически.");
                break;
        }

        return steps;
    }

    private static string BuildStatusBasis(
        SignalKnowledgeState status,
        bool hasExplicitConfirmation,
        bool hasCrossIncidentEvidence,
        bool hasIndependentEvidence) =>
        status switch
        {
            SignalKnowledgeState.Unknown =>
                "UNKNOWN сохранён в профиле: назначение сигнала ещё не классифицировано.",

            SignalKnowledgeState.Candidate =>
                "CANDIDATE сохранён в профиле: есть гипотеза/наблюдаемая корреляция, но этого недостаточно для инженерного подтверждения назначения.",

            SignalKnowledgeState.Probable when hasCrossIncidentEvidence && hasIndependentEvidence =>
                "PROBABLE сохранён в профиле; присутствуют повторяемые Cross-Incident evidence и независимый источник. Статус всё равно не переводится в CONFIRMED автоматически.",

            SignalKnowledgeState.Probable when hasCrossIncidentEvidence =>
                "PROBABLE сохранён в профиле; Cross-Incident evidence показывает повторяемость наблюдаемого CAN-шагa. Физическая причинность этим не доказана.",

            SignalKnowledgeState.Probable =>
                "PROBABLE сохранён в профиле: evidence сильнее обычного кандидата, но окончательное назначение должно подтверждаться отдельно.",

            SignalKnowledgeState.Confirmed when hasExplicitConfirmation =>
                "CONFIRMED сохранён в профиле и присутствует UserConfirmation: это фиксирует явное инженерное подтверждение. Explainer не пытается заново вычислять или отменять этот статус.",

            SignalKnowledgeState.Confirmed =>
                "CONFIRMED сохранён в профиле, но явное UserConfirmation в evidence не найдено; требуется проверка происхождения статуса.",

            SignalKnowledgeState.Rejected =>
                "REJECTED сохранён в профиле: кандидат был отклонён и не должен автоматически возвращаться в рабочие сигналы.",

            _ =>
                "Статус прочитан из Machine Profile без автоматического изменения."
        };

    private static bool IsIndependentEvidence(EvidenceKind kind) => kind is
        EvidenceKind.ElectricalSchematic or
        EvidenceKind.HydraulicSchematic or
        EvidenceKind.ManufacturerDocumentation or
        EvidenceKind.Dbc or
        EvidenceKind.J1939Database or
        EvidenceKind.ControllerConfiguration or
        EvidenceKind.Mpf or
        EvidenceKind.ServiceSoftware or
        EvidenceKind.MultimeterMeasurement or
        EvidenceKind.CurrentMeasurement or
        EvidenceKind.PhysicalOutputCheck;

    private static bool IsReplayEvidence(SignalEvidence evidence)
    {
        if (string.Equals(
                evidence.CaptureOrigin,
                "replay",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return evidence.Captures.Count > 0 &&
               evidence.Captures.All(capture =>
                   string.Equals(
                       capture.DriverId,
                       "pcan-trc-replay",
                       StringComparison.OrdinalIgnoreCase));
    }

    private static int EvidenceOrder(EvidenceKind kind) => kind switch
    {
        EvidenceKind.UserConfirmation => 0,
        EvidenceKind.PhysicalOutputCheck => 10,
        EvidenceKind.MultimeterMeasurement => 11,
        EvidenceKind.CurrentMeasurement => 12,
        EvidenceKind.ElectricalSchematic => 20,
        EvidenceKind.HydraulicSchematic => 21,
        EvidenceKind.ManufacturerDocumentation => 22,
        EvidenceKind.ControllerConfiguration => 23,
        EvidenceKind.Mpf => 24,
        EvidenceKind.ServiceSoftware => 25,
        EvidenceKind.Dbc => 26,
        EvidenceKind.J1939Database => 27,
        EvidenceKind.CrossIncidentSignature => 40,
        EvidenceKind.RepeatedExperiment => 41,
        EvidenceKind.IncidentCapture => 42,
        _ => 100
    };

    private static string EvidenceTitle(EvidenceKind kind) => kind switch
    {
        EvidenceKind.RepeatedExperiment => "Повторяемый REFERENCE/ACTION эксперимент",
        EvidenceKind.ElectricalSchematic => "Электрическая схема",
        EvidenceKind.HydraulicSchematic => "Гидравлическая схема",
        EvidenceKind.ManufacturerDocumentation => "Документация производителя",
        EvidenceKind.Dbc => "DBC",
        EvidenceKind.J1939Database => "J1939 database",
        EvidenceKind.ControllerConfiguration => "Конфигурация контроллера",
        EvidenceKind.Mpf => "MPF / параметрический файл",
        EvidenceKind.ServiceSoftware => "Сервисное ПО",
        EvidenceKind.MultimeterMeasurement => "Измерение мультиметром",
        EvidenceKind.CurrentMeasurement => "Измерение тока",
        EvidenceKind.PhysicalOutputCheck => "Физическая проверка выхода",
        EvidenceKind.UserConfirmation => "Явное подтверждение пользователя/инженера",
        EvidenceKind.IncidentCapture => "Incident capture",
        EvidenceKind.CrossIncidentSignature => "Cross-Incident Signature",
        _ => kind.ToString()
    };

    private static string EvidenceInterpretation(EvidenceKind kind) => kind switch
    {
        EvidenceKind.RepeatedExperiment =>
            "Повторяемая CAN-корреляция в управляемом эксперименте; сама по себе не доказывает назначение ECU-сигнала.",
        EvidenceKind.IncidentCapture =>
            "Наблюдение вокруг события/неисправности; одиночная запись не равна повторяемости.",
        EvidenceKind.CrossIncidentSignature =>
            "Повторяемость одного CAN-шагa между несколькими incident; сильное наблюдаемое evidence, но не доказательство физической причинности.",
        EvidenceKind.UserConfirmation =>
            "Фиксирует явное инженерное решение о статусе; полезно для трассируемости, но не заменяет источник измерения/документации.",
        EvidenceKind.ElectricalSchematic or EvidenceKind.HydraulicSchematic =>
            "Независимая схема может связать наблюдаемый сигнал с реальным входом/выходом или цепью машины.",
        EvidenceKind.ManufacturerDocumentation or EvidenceKind.Dbc or EvidenceKind.J1939Database =>
            "Независимый документированный источник назначения/формата сигнала.",
        EvidenceKind.ControllerConfiguration or EvidenceKind.Mpf or EvidenceKind.ServiceSoftware =>
            "Независимый источник из конфигурации/сервисной среды контроллера; требуется проверять соответствие конкретной машине и версии.",
        EvidenceKind.MultimeterMeasurement or EvidenceKind.CurrentMeasurement or EvidenceKind.PhysicalOutputCheck =>
            "Независимая физическая/электрическая проверка реакции машины, усиливающая CAN-наблюдение.",
        _ =>
            "Сохранённый источник evidence; его инженерную силу нужно оценивать по происхождению и воспроизводимости."
    };
}
