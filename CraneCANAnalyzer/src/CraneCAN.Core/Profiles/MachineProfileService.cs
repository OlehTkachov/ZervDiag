using CraneCAN.Core.Guided;

namespace CraneCAN.Core.Profiles;

public static class MachineProfileService
{
    public static MachineProfile AddCandidate(
        MachineProfile profile,
        GuidedCandidate candidate,
        string name,
        SignalKnowledgeState status,
        string? notes = null,
        GuidedExperiment? experiment = null,
        string? experimentPath = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (status is SignalKnowledgeState.Unknown or SignalKnowledgeState.Rejected)
        {
            throw new ArgumentOutOfRangeException(nameof(status),
                "Для добавления сигнала используйте CANDIDATE, PROBABLE или CONFIRMED.");
        }

        var captures = experiment?.LiveCaptures ?? [];
        var hasReplay = captures.Any(item => item.DriverId == "pcan-trc-replay");
        var origin = captures.Count == 0 ? "unknown" :
            captures.All(item => item.DriverId == "pcan-trc-replay") ? "replay" :
            captures.All(item => item.DriverId == "peak-pcan-basic") ? "livePcan" : "mixedOrUnknown";
        var evidence = new SignalEvidence
        {
            ExperimentId = experiment?.ExperimentId,
            ExperimentPath = experimentPath,
            CaptureOrigin = origin,
            Repeats = experiment?.Repeats.ToList() ?? [],
            Captures = captures.ToList(),
            Kind = EvidenceKind.RepeatedExperiment,
            Description = $"Повторяемость {candidate.RepeatabilityCount}/{candidate.RepeatCount}; score {candidate.Score}/100. " +
                (hasReplay ? "REPLAY: воспроизведение записи; независимые физические опыты не подтверждены." :
                 "Независимость физических опытов требует проверки источников."),
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
            ProgramVersion = "0.7.0",
            Bitrate = ResolveBitrate(profile, experiment),
            KnownSignals = known,
            ExperimentalSignals = experimental,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static int? ResolveBitrate(MachineProfile profile, GuidedExperiment? experiment)
    {
        // Replay bitrate is a UI setting, not evidence of the physical bus bitrate.
        if (experiment is null || experiment.Bus != profile.CanBusName || experiment.LiveCaptures.Count == 0)
            return profile.Bitrate;
        if (experiment.LiveCaptures.Any(item => item.DriverId != "peak-pcan-basic" || item.Bitrate <= 0))
            return profile.Bitrate;
        var rates = experiment.LiveCaptures.Select(item => item.Bitrate).Distinct().ToArray();
        if (rates.Length != 1 || (profile.Bitrate.HasValue && profile.Bitrate != rates[0])) return profile.Bitrate;
        return rates[0];
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
