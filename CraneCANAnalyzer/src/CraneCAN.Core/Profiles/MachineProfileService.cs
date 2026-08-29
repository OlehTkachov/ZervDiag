using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Profiles;

public static class MachineProfileService
{
    public static MachineProfile AddCandidate(
        MachineProfile profile,
        GuidedCandidate candidate,
        string name,
        SignalKnowledgeState status,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (status is SignalKnowledgeState.Unknown or SignalKnowledgeState.Rejected)
        {
            throw new ArgumentOutOfRangeException(nameof(status),
                "Для добавления сигнала используйте CANDIDATE, PROBABLE или CONFIRMED.");
        }

        var evidence = new SignalEvidence
        {
            Kind = EvidenceKind.RepeatedExperiment,
            Description = $"Повторяемость {candidate.RepeatabilityCount}/{candidate.RepeatCount}; score {candidate.Score}/100",
            SourceReference = candidate.StableKey
        };
        var evidenceItems = new List<SignalEvidence> { evidence };
        if (status == SignalKnowledgeState.Confirmed)
        {
            evidenceItems.Add(new SignalEvidence
            {
                Kind = EvidenceKind.UserConfirmation,
                Description = "Пользователь явно установил статус CONFIRMED.",
                SourceReference = candidate.StableKey
            });
        }

        var signal = new MachineSignal
        {
            Name = name.Trim(),
            Description = $"Кандидат, коррелирующий с действием «{candidate.ActionName}».",
            CanId = candidate.Id,
            IsExtended = candidate.IsExtended,
            StartByte = candidate.DataIndex ?? 0,
            StartBit = candidate.BitIndex ?? 0,
            BitLength = candidate.BitIndex.HasValue ? 1 : 8,
            Confidence = status,
            Evidence = evidenceItems,
            Source = "Guided Diagnostics experiment",
            Notes = notes?.Trim() ?? string.Empty
        };

        var known = profile.KnownSignals.ToList();
        var experimental = profile.ExperimentalSignals.ToList();
        var existing = known.Concat(experimental).FirstOrDefault(item =>
            item.CanId == signal.CanId &&
            item.IsExtended == signal.IsExtended &&
            item.StartByte == signal.StartByte &&
            item.StartBit == signal.StartBit &&
            item.BitLength == signal.BitLength);
        if (existing is not null)
        {
            known.RemoveAll(item => item.SignalId == existing.SignalId);
            experimental.RemoveAll(item => item.SignalId == existing.SignalId);
            signal = signal with
            {
                SignalId = existing.SignalId,
                Evidence = existing.Evidence.Concat(signal.Evidence).ToList(),
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        if (status == SignalKnowledgeState.Confirmed)
        {
            known.Add(signal);
        }
        else
        {
            experimental.Add(signal);
        }

        return profile with
        {
            KnownSignals = known,
            ExperimentalSignals = experimental,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static MachineProfile PromoteSignal(
        MachineProfile profile,
        Guid signalId,
        SignalKnowledgeState newStatus,
        SignalEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(evidence);
        var all = profile.ExperimentalSignals.Concat(profile.KnownSignals).ToList();
        var existing = all.SingleOrDefault(signal => signal.SignalId == signalId)
            ?? throw new KeyNotFoundException("Сигнал отсутствует в профиле.");
        var updated = existing with
        {
            Confidence = newStatus,
            Evidence = existing.Evidence.Append(evidence).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var experimental = all
            .Where(signal => signal.SignalId != signalId && signal.Confidence != SignalKnowledgeState.Confirmed)
            .ToList();
        var known = all
            .Where(signal => signal.SignalId != signalId && signal.Confidence == SignalKnowledgeState.Confirmed)
            .ToList();
        if (newStatus == SignalKnowledgeState.Confirmed)
        {
            known.Add(updated);
        }
        else
        {
            experimental.Add(updated);
        }

        return profile with
        {
            ExperimentalSignals = experimental,
            KnownSignals = known,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
