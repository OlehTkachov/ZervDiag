using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Profiles;

public sealed record IncidentSignalDefinition
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

public sealed record IncidentSignalProfileUpdate(
    MachineProfile Profile,
    MachineSignal Signal,
    bool ExistingSignalUpdated);

public static class IncidentSignalProfileService
{
    public static IncidentSignalProfileUpdate AddOrUpdate(
        MachineProfile profile,
        IncidentEventChainStep step,
        DateTimeOffset markerTime,
        IncidentSignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(definition);

        if (step.Kind != IncidentTransitionKind.ByteChanged || !step.DataIndex.HasValue)
            throw new InvalidOperationException(
                "Signal Builder из incident разрешён только для наблюдаемого изменения DATA[n].");

        ValidateDefinition(step, definition);

        var sourceReference =
            $"incident:{markerTime:O}:{(step.IsExtended ? "E" : "S")}:{step.Id:X8}:DATA[{step.DataIndex.Value}]";

        var evidence = new SignalEvidence
        {
            CaptureOrigin = "incident",
            Kind = EvidenceKind.IncidentCapture,
            Description =
                $"Incident Event Chain: {step.Priority}; изменение {step.BaselineValue} → {step.ObservedValue} " +
                $"в {step.ReactionMilliseconds:+0.###;-0.###;0} ms относительно marker. " +
                "Это одиночное наблюдение incident; независимая повторяемость не подтверждена.",
            SourceReference = sourceReference
        };

        var known = profile.KnownSignals.ToList();
        var experimental = profile.ExperimentalSignals.ToList();
        var existing = known.Concat(experimental).FirstOrDefault(signal =>
            signal.CanId == step.Id &&
            signal.IsExtended == step.IsExtended &&
            signal.StartByte == definition.StartByte &&
            signal.StartBit == definition.StartBit &&
            signal.BitLength == definition.BitLength);

        if (existing is not null)
        {
            var evidenceItems = existing.Evidence.Any(item =>
                    item.Kind == evidence.Kind &&
                    string.Equals(item.SourceReference, evidence.SourceReference, StringComparison.Ordinal))
                ? existing.Evidence.ToList()
                : existing.Evidence.Append(evidence).ToList();

            var updated = existing with
            {
                Evidence = evidenceItems,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            known.RemoveAll(signal => signal.SignalId == existing.SignalId);
            experimental.RemoveAll(signal => signal.SignalId == existing.SignalId);
            if (updated.Confidence == SignalKnowledgeState.Confirmed)
                known.Add(updated);
            else
                experimental.Add(updated);

            return new IncidentSignalProfileUpdate(
                profile with
                {
                    ProgramVersion = "0.7.0",
                    KnownSignals = known,
                    ExperimentalSignals = experimental,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                updated,
                true);
        }

        var signal = new MachineSignal
        {
            Name = definition.Name.Trim(),
            Description =
                "Пользовательская интерпретация наблюдаемого DATA-перехода из Incident Event Chain.",
            CanId = step.Id,
            IsExtended = step.IsExtended,
            StartByte = definition.StartByte,
            StartBit = definition.StartBit,
            BitLength = definition.BitLength,
            ByteOrder = definition.ByteOrder,
            IsSigned = definition.IsSigned,
            Scale = definition.Scale,
            Offset = definition.Offset,
            Unit = definition.Unit.Trim(),
            Confidence = SignalKnowledgeState.Candidate,
            Evidence = [evidence],
            Source = "Incident Event Chain / Signal Builder",
            Notes = definition.Notes.Trim()
        };

        experimental.Add(signal);
        return new IncidentSignalProfileUpdate(
            profile with
            {
                ProgramVersion = "0.7.0",
                KnownSignals = known,
                ExperimentalSignals = experimental,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            signal,
            false);
    }

    private static void ValidateDefinition(
        IncidentEventChainStep step,
        IncidentSignalDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

        if (definition.StartByte is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(definition.StartByte),
                "StartByte должен быть в диапазоне 0…7.");
        if (definition.StartBit is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(definition.StartBit),
                "StartBit должен быть в диапазоне 0…7.");
        if (definition.BitLength is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(definition.BitLength),
                "BitLength должен быть в диапазоне 1…64.");

        var firstBit = checked(definition.StartByte * 8 + definition.StartBit);
        var lastBitExclusive = checked(firstBit + definition.BitLength);
        if (lastBitExclusive > 64)
            throw new ArgumentOutOfRangeException(nameof(definition.BitLength),
                "Поле выходит за пределы Classical CAN DATA[0…7].");

        var selectedByte = step.DataIndex!.Value;
        var firstByte = firstBit / 8;
        var lastByte = (lastBitExclusive - 1) / 8;
        if (selectedByte < firstByte || selectedByte > lastByte)
            throw new ArgumentException(
                $"Выбранное поле не охватывает изменившийся DATA[{selectedByte}].");

        if (!double.IsFinite(definition.Scale) || definition.Scale == 0)
            throw new ArgumentOutOfRangeException(nameof(definition.Scale),
                "Scale должен быть конечным ненулевым числом.");
        if (!double.IsFinite(definition.Offset))
            throw new ArgumentOutOfRangeException(nameof(definition.Offset),
                "Offset должен быть конечным числом.");

        var maximumId = step.IsExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (step.Id > maximumId)
            throw new ArgumentOutOfRangeException(nameof(step.Id),
                "CAN ID не соответствует Standard/Extended формату.");
    }
}
