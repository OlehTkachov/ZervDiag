using System.Globalization;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Profiles;

public sealed record IncidentSignatureSignalDefinition
{
    public string Name { get; init; } = string.Empty;
    public int StartByte { get; init; }
    public int StartBit { get; init; }
    public int BitLength { get; init; } = 8;
    public SignalByteOrder ByteOrder { get; init; } = SignalByteOrder.LittleEndian;
    public bool IsSigned { get; init; }
    public double Scale { get; init; } = 1;
    public double Offset { get; init; }
    public string Unit { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

public sealed record IncidentSignatureProfileUpdate(
    MachineProfile Profile,
    MachineSignal Signal,
    bool ExistingSignalUpdated,
    SignalKnowledgeState PreviousConfidence,
    SignalKnowledgeState ResultingConfidence);

public static class IncidentSignatureProfileService
{
    public static IncidentSignatureProfileUpdate AddOrUpdate(
        MachineProfile profile,
        IncidentSignatureCandidate candidate,
        IReadOnlyList<Guid> incidentIds,
        IncidentSignatureSignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(incidentIds);
        ArgumentNullException.ThrowIfNull(definition);

        if (candidate.Kind != IncidentTransitionKind.ByteChanged ||
            !candidate.DataIndex.HasValue)
        {
            throw new InvalidOperationException(
                "Cross-Incident evidence можно привязать к Machine Profile только для наблюдаемого DATA[n]-изменения.");
        }

        if (candidate.Priority == IncidentSignaturePriority.Info)
        {
            throw new InvalidOperationException(
                "INFO-сигнатура недостаточно повторяема для добавления в Machine Profile. Используйте MEDIUM или HIGH.");
        }

        var distinctIncidentIds = incidentIds
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (distinctIncidentIds.Length < 3)
        {
            throw new ArgumentException(
                "Cross-Incident evidence требует минимум три разных Incident ID.",
                nameof(incidentIds));
        }

        if (candidate.IncidentCount != distinctIncidentIds.Length)
        {
            throw new ArgumentException(
                $"Кандидат рассчитан по {candidate.IncidentCount} incident, а передано {distinctIncidentIds.Length} уникальных Incident ID.",
                nameof(incidentIds));
        }

        if (candidate.OccurrenceCount is < 1 ||
            candidate.OccurrenceCount > candidate.IncidentCount ||
            candidate.TransitionAgreementCount is < 1 ||
            candidate.TransitionAgreementCount > candidate.OccurrenceCount)
        {
            throw new ArgumentException(
                "Некорректные счётчики repeatability/transition agreement в Cross-Incident кандидате.",
                nameof(candidate));
        }

        ValidateDefinition(candidate, definition);

        var recommended = candidate.Priority == IncidentSignaturePriority.High
            ? SignalKnowledgeState.Probable
            : SignalKnowledgeState.Candidate;

        var sourceReference = BuildSourceReference(candidate, distinctIncidentIds);
        var evidence = new SignalEvidence
        {
            CaptureOrigin = "incident-series",
            Kind = EvidenceKind.CrossIncidentSignature,
            Description =
                $"Cross-Incident Signature: {candidate.Priority}; " +
                $"повторяемость {candidate.OccurrenceCount}/{candidate.IncidentCount} " +
                $"({candidate.RepeatabilityPercent.ToString("0.#", CultureInfo.InvariantCulture)}%); " +
                $"модальный переход {candidate.ModalBaselineValue} → {candidate.ModalObservedValue}; " +
                $"согласие перехода {candidate.TransitionAgreementCount}/{candidate.OccurrenceCount} " +
                $"({candidate.TransitionAgreementPercent.ToString("0.#", CultureInfo.InvariantCulture)}%); " +
                $"median t {candidate.MedianReactionMilliseconds.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture)} ms; " +
                $"разброс {candidate.TimingSpreadMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms. " +
                "Повторяемость CAN-наблюдения не является автоматическим доказательством физической причинности.",
            SourceReference = sourceReference
        };

        var known = profile.KnownSignals.ToList();
        var experimental = profile.ExperimentalSignals.ToList();
        var existing = known
            .Concat(experimental)
            .FirstOrDefault(signal =>
                signal.CanId == candidate.Id &&
                signal.IsExtended == candidate.IsExtended &&
                signal.StartByte == definition.StartByte &&
                signal.StartBit == definition.StartBit &&
                signal.BitLength == definition.BitLength);

        if (existing is not null)
        {
            if (existing.Confidence == SignalKnowledgeState.Rejected)
            {
                throw new InvalidOperationException(
                    "Совпадающий сигнал помечен REJECTED. Cross-Incident evidence не может автоматически восстановить отклонённый сигнал.");
            }

            var previous = existing.Confidence;
            var resulting = Stronger(previous, recommended);
            if (resulting == SignalKnowledgeState.Confirmed)
                resulting = SignalKnowledgeState.Confirmed;

            var evidenceItems = existing.Evidence.Any(item =>
                    item.Kind == EvidenceKind.CrossIncidentSignature &&
                    string.Equals(
                        item.SourceReference,
                        sourceReference,
                        StringComparison.Ordinal))
                ? existing.Evidence.ToList()
                : existing.Evidence.Append(evidence).ToList();

            var updated = existing with
            {
                Confidence = resulting,
                Evidence = evidenceItems,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            known.RemoveAll(signal => signal.SignalId == existing.SignalId);
            experimental.RemoveAll(signal => signal.SignalId == existing.SignalId);

            if (updated.Confidence == SignalKnowledgeState.Confirmed)
                known.Add(updated);
            else
                experimental.Add(updated);

            return new IncidentSignatureProfileUpdate(
                profile with
                {
                    ProgramVersion = "0.7.0",
                    KnownSignals = known,
                    ExperimentalSignals = experimental,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                updated,
                true,
                previous,
                updated.Confidence);
        }

        var signal = new MachineSignal
        {
            Name = definition.Name.Trim(),
            Description =
                "Пользовательская интерпретация повторяемого DATA-перехода из Cross-Incident Signature.",
            CanId = candidate.Id,
            IsExtended = candidate.IsExtended,
            StartByte = definition.StartByte,
            StartBit = definition.StartBit,
            BitLength = definition.BitLength,
            ByteOrder = definition.ByteOrder,
            IsSigned = definition.IsSigned,
            Scale = definition.Scale,
            Offset = definition.Offset,
            Unit = definition.Unit.Trim(),
            Confidence = recommended,
            Evidence = [evidence],
            Source = "Cross-Incident Signature",
            Notes = definition.Notes.Trim()
        };

        experimental.Add(signal);
        return new IncidentSignatureProfileUpdate(
            profile with
            {
                ProgramVersion = "0.7.0",
                KnownSignals = known,
                ExperimentalSignals = experimental,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            signal,
            false,
            SignalKnowledgeState.Unknown,
            signal.Confidence);
    }

    private static SignalKnowledgeState Stronger(
        SignalKnowledgeState current,
        SignalKnowledgeState recommended)
    {
        static int Rank(SignalKnowledgeState state) => state switch
        {
            SignalKnowledgeState.Unknown => 0,
            SignalKnowledgeState.Candidate => 1,
            SignalKnowledgeState.Probable => 2,
            SignalKnowledgeState.Confirmed => 3,
            SignalKnowledgeState.Rejected => -1,
            _ => 0
        };

        return Rank(current) >= Rank(recommended)
            ? current
            : recommended;
    }

    private static string BuildSourceReference(
        IncidentSignatureCandidate candidate,
        IReadOnlyList<Guid> incidentIds)
    {
        var series = string.Join(
            ",",
            incidentIds.Select(id => id.ToString("N", CultureInfo.InvariantCulture)));

        return
            $"incident-signature:{candidate.Id:X8}:{(candidate.IsExtended ? "E" : "S")}:" +
            $"DATA[{candidate.DataIndex!.Value}]:{candidate.Kind}:{series}";
    }

    private static void ValidateDefinition(
        IncidentSignatureCandidate candidate,
        IncidentSignatureSignalDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

        if (definition.StartByte is < 0 or > 7)
            throw new ArgumentOutOfRangeException(
                nameof(definition.StartByte),
                "StartByte должен быть в диапазоне 0…7.");
        if (definition.StartBit is < 0 or > 7)
            throw new ArgumentOutOfRangeException(
                nameof(definition.StartBit),
                "StartBit должен быть в диапазоне 0…7.");
        if (definition.BitLength is < 1 or > 64)
            throw new ArgumentOutOfRangeException(
                nameof(definition.BitLength),
                "BitLength должен быть в диапазоне 1…64.");

        var firstBit = checked(definition.StartByte * 8 + definition.StartBit);
        var lastBitExclusive = checked(firstBit + definition.BitLength);
        if (lastBitExclusive > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition.BitLength),
                "Поле выходит за пределы Classical CAN DATA[0…7].");
        }

        var selectedByte = candidate.DataIndex!.Value;
        var firstByte = firstBit / 8;
        var lastByte = (lastBitExclusive - 1) / 8;
        if (selectedByte < firstByte || selectedByte > lastByte)
        {
            throw new ArgumentException(
                $"Выбранное поле не охватывает повторяемо изменившийся DATA[{selectedByte}].");
        }

        if (!double.IsFinite(definition.Scale) || definition.Scale == 0)
            throw new ArgumentOutOfRangeException(
                nameof(definition.Scale),
                "Scale должен быть конечным ненулевым числом.");
        if (!double.IsFinite(definition.Offset))
            throw new ArgumentOutOfRangeException(
                nameof(definition.Offset),
                "Offset должен быть конечным числом.");

        var maximumId = candidate.IsExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (candidate.Id > maximumId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidate.Id),
                "CAN ID не соответствует Standard/Extended формату.");
        }
    }
}
