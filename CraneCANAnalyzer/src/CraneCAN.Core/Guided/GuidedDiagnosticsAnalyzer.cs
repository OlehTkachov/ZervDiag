using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Guided;

public static class GuidedDiagnosticsAnalyzer
{
    private sealed record RunCandidate(
        GenericCanComparison Comparison,
        CandidateChangeKind Kind,
        int? BitIndex,
        TemporalTransition? Temporal,
        int RepeatNumber);

    public static GuidedAnalysisResult Analyze(
        string actionName,
        IReadOnlyList<GuidedExperimentRun> runs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(runs);

        var quality = ExperimentQualityAnalyzer.Evaluate(runs);
        if (!quality.CanAnalyze)
        {
            return new GuidedAnalysisResult(quality, [], [], new Dictionary<int, DateTimeOffset?>());
        }

        var detectedEvents = new Dictionary<int, DateTimeOffset?>();
        var runCandidates = new List<RunCandidate>();
        var timeline = new List<GuidedTimelineEvent>();
        var addedQualityIssues = quality.Issues.ToList();

        foreach (var run in runs.OrderBy(run => run.RepeatNumber))
        {
            var referenceFrames = Comparable(run.ReferenceFrames);
            var actionFrames = Comparable(run.ActionFrames);
            var eventTime = run.ApproximateEventTime.HasValue
                ? DetectEventTimeNear(
                    referenceFrames,
                    actionFrames,
                    run.ApproximateEventTime.Value,
                    run.EventSearchTolerance ?? TimeSpan.FromSeconds(2)) ?? run.ApproximateEventTime
                : DetectEventTime(referenceFrames, actionFrames);
            detectedEvents[run.RepeatNumber] = eventTime;
            if (!eventTime.HasValue)
            {
                addedQualityIssues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.EventNotDetected,
                    ExperimentQualitySeverity.Warning,
                    run.RepeatNumber));
            }
            else
            {
                timeline.Add(new GuidedTimelineEvent
                {
                    Timestamp = eventTime.Value,
                    EventType = "ActionBoundary",
                    CandidateKey = string.Empty,
                    RepeatNumber = run.RepeatNumber
                });
            }

            var comparisons = GenericExperimentComparator.Compare(referenceFrames, actionFrames);
            foreach (var comparison in comparisons.Where(row => row.IsChanged))
            {
                var bitIndex = SingleBitIndex(comparison.XorMask);
                var temporal = comparison.DataIndex.HasValue
                    ? AnalyzeTemporal(comparison, actionFrames, run.ReturnFrames, eventTime)
                    : AnalyzePresenceTemporal(comparison, actionFrames, eventTime);
                var kind = Classify(comparison, temporal, bitIndex);
                runCandidates.Add(new RunCandidate(
                    comparison, kind, bitIndex, temporal, run.RepeatNumber));

                if (temporal?.FirstChangeTime is { } changeTime)
                {
                    timeline.Add(new GuidedTimelineEvent
                    {
                        Timestamp = changeTime,
                        EventType = "CandidateChanged",
                        CandidateKey = StableKey(comparison, bitIndex),
                        Id = comparison.Id,
                        IsExtended = comparison.IsExtended,
                        DataIndex = comparison.DataIndex,
                        Value = temporal.FirstChangedValue,
                        RepeatNumber = run.RepeatNumber
                    });
                }
            }
        }

        var candidates = runCandidates
            .GroupBy(candidate => StableKey(candidate.Comparison, candidate.BitIndex))
            .Select(group => Aggregate(actionName, group.ToArray(), runs.Count))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.RepeatabilityCount)
            .ThenBy(candidate => candidate.Id)
            .ThenBy(candidate => candidate.IsExtended)
            .ThenBy(candidate => candidate.DataIndex ?? -1)
            .ToArray();

        if (runs.Count > 1 && candidates.Length > 0 && candidates.All(candidate => candidate.RepeatabilityCount < runs.Count))
        {
            addedQualityIssues.Add(new ExperimentQualityIssue(
                ExperimentQualityCode.LowRepeatability, ExperimentQualitySeverity.Warning));
        }

        quality = new ExperimentQualityReport(addedQualityIssues);
        return new GuidedAnalysisResult(
            quality,
            candidates,
            timeline.OrderBy(item => item.Timestamp).ToArray(),
            detectedEvents);
    }

    private static GuidedCandidate Aggregate(
        string actionName,
        IReadOnlyList<RunCandidate> observations,
        int repeatCount)
    {
        var representative = observations
            .OrderByDescending(item => Math.Min(
                item.Comparison.ReferenceAgreementPercent,
                item.Comparison.ActionAgreementPercent))
            .First();
        var repeatNumbers = observations.Select(item => item.RepeatNumber).Distinct().Order().ToArray();
        var repeatability = repeatNumbers.Length;
        var agreement = observations.Average(item => Math.Min(
            item.Comparison.ReferenceAgreementPercent,
            item.Comparison.ActionAgreementPercent));
        var reactions = observations
            .Select(item => item.Temporal?.ReactionMilliseconds)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var reaction = reactions.Length == 0 ? (double?)null : reactions.Average();
        var returned = observations.Any(item => item.Temporal?.ReturnedToBaseline == true);
        var kind = observations
            .GroupBy(item => item.Kind)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First().Key;
        var scoreExplanation = Score(kind, repeatability, repeatCount, agreement, reaction, returned);

        return new GuidedCandidate
        {
            Id = representative.Comparison.Id,
            IsExtended = representative.Comparison.IsExtended,
            DataIndex = representative.Comparison.DataIndex,
            BitIndex = representative.BitIndex,
            ReferenceValue = representative.Comparison.ReferenceValue,
            ActionValue = representative.Comparison.ActionValue,
            ChangeKind = kind,
            ActionName = actionName,
            RepeatabilityCount = repeatability,
            RepeatCount = repeatCount,
            AgreementPercent = agreement,
            ReactionMilliseconds = reaction,
            Temporal = representative.Temporal,
            Score = Math.Clamp(scoreExplanation.Sum(item => item.Points), 0, 100),
            ScoreExplanation = scoreExplanation,
            ObservedInRepeats = repeatNumbers
        };
    }

    private static IReadOnlyList<ScoreContribution> Score(
        CandidateChangeKind kind,
        int repeatability,
        int repeatCount,
        double agreement,
        double? reactionMilliseconds,
        bool returned)
    {
        var result = new List<ScoreContribution>();
        if (repeatCount >= 3 && repeatability == repeatCount)
        {
            result.Add(new ScoreContribution(ScoreReason.RepeatedAllThreeOrMore, 30));
        }
        else if (repeatability == repeatCount)
        {
            result.Add(new ScoreContribution(ScoreReason.RepeatedAllAvailable, 15));
        }
        else if (repeatability * 2 < repeatCount)
        {
            result.Add(new ScoreContribution(ScoreReason.LowRepeatability, -30));
        }

        switch (kind)
        {
            case CandidateChangeKind.StableBit:
                result.Add(new ScoreContribution(ScoreReason.StableBitTransition, 25));
                break;
            case CandidateChangeKind.StableByte:
            case CandidateChangeKind.DlcChanged:
                result.Add(new ScoreContribution(ScoreReason.StableByteTransition, 18));
                break;
            case CandidateChangeKind.MessageAppeared:
            case CandidateChangeKind.MessageDisappeared:
                result.Add(new ScoreContribution(ScoreReason.MessagePresenceChange, 25));
                break;
            case CandidateChangeKind.Ramp:
                result.Add(new ScoreContribution(ScoreReason.AnalogRamp, 12));
                break;
            case CandidateChangeKind.AnalogNoise:
                result.Add(new ScoreContribution(ScoreReason.AnalogNoise, -25));
                break;
        }

        if (reactionMilliseconds >= 0)
        {
            result.Add(new ScoreContribution(ScoreReason.OccursAfterAction, 20));
        }
        else if (reactionMilliseconds < 0)
        {
            result.Add(new ScoreContribution(ScoreReason.OccursBeforeAction, -20));
        }

        if (returned)
        {
            result.Add(new ScoreContribution(ScoreReason.ReturnsToBaseline, 15));
        }

        if (agreement >= 95)
        {
            result.Add(new ScoreContribution(ScoreReason.AgreementAbove95, 10));
        }

        return result;
    }

    private static CandidateChangeKind Classify(
        GenericCanComparison comparison,
        TemporalTransition? temporal,
        int? bitIndex)
    {
        if (comparison.MessageAppeared)
        {
            return CandidateChangeKind.MessageAppeared;
        }

        if (comparison.MessageDisappeared)
        {
            return CandidateChangeKind.MessageDisappeared;
        }

        if (!comparison.ReferenceValue.HasValue || !comparison.ActionValue.HasValue)
        {
            return CandidateChangeKind.DlcChanged;
        }

        if (bitIndex.HasValue &&
            comparison.ReferenceAgreementPercent >= GenericExperimentComparator.StableAgreementThresholdPercent &&
            comparison.ActionAgreementPercent >= GenericExperimentComparator.StableAgreementThresholdPercent)
        {
            return CandidateChangeKind.StableBit;
        }

        if (temporal is { Sequence.Count: >= 3, MonotonicityPercent: >= 75 } &&
            temporal.MinimumValue.HasValue && temporal.MaximumValue.HasValue &&
            temporal.MaximumValue.Value - temporal.MinimumValue.Value >= 3)
        {
            return CandidateChangeKind.Ramp;
        }

        if (comparison.Kind == GenericCanChangeKind.UnstableOrAnalogNoise)
        {
            return CandidateChangeKind.AnalogNoise;
        }

        return CandidateChangeKind.StableByte;
    }

    private static TemporalTransition AnalyzeTemporal(
        GenericCanComparison comparison,
        IReadOnlyList<CanFrame> actionFrames,
        IReadOnlyList<CanFrame>? returnFrames,
        DateTimeOffset? eventTime)
    {
        var dataIndex = comparison.DataIndex!.Value;
        var samples = actionFrames
            .Where(frame => frame.Id == comparison.Id &&
                            frame.IsExtended == comparison.IsExtended &&
                            frame.Data.Length > dataIndex)
            .OrderBy(frame => frame.Timestamp)
            .Select(frame => (frame.Timestamp, Value: frame.Data[dataIndex]))
            .ToArray();
        var baseline = comparison.ReferenceValue;
        var firstChanged = samples.FirstOrDefault(sample => sample.Value != baseline);
        var hasChanged = samples.Any(sample => sample.Value != baseline);
        var sequence = Collapse(samples.Select(sample => sample.Value));
        var lastStable = Mode(samples.Skip(Math.Max(0, samples.Length * 3 / 4)).Select(sample => sample.Value));
        var stabilization = FindStabilization(samples, lastStable, eventTime);
        var returned = ReturnContainsBaseline(returnFrames, comparison, baseline) ||
                       (sequence.Count > 1 && sequence[^1] == baseline);

        return new TemporalTransition
        {
            BaselineValue = baseline,
            FirstChangedValue = hasChanged ? firstChanged.Value : null,
            FirstChangeTime = hasChanged ? firstChanged.Timestamp : null,
            ReactionMilliseconds = hasChanged && eventTime.HasValue
                ? (firstChanged.Timestamp - eventTime.Value).TotalMilliseconds
                : null,
            Sequence = sequence,
            MinimumValue = samples.Length == 0 ? null : samples.Min(sample => sample.Value),
            MaximumValue = samples.Length == 0 ? null : samples.Max(sample => sample.Value),
            LastStableValue = lastStable,
            StabilizationMilliseconds = stabilization,
            ReturnedToBaseline = returned,
            RatePerSecond = CalculateRate(samples),
            MonotonicityPercent = CalculateMonotonicity(sequence),
            TransitionCount = Math.Max(0, sequence.Count - 1)
        };
    }

    private static TemporalTransition AnalyzePresenceTemporal(
        GenericCanComparison comparison,
        IReadOnlyList<CanFrame> actionFrames,
        DateTimeOffset? eventTime)
    {
        var first = actionFrames
            .Where(frame => frame.Id == comparison.Id && frame.IsExtended == comparison.IsExtended)
            .OrderBy(frame => frame.Timestamp)
            .Select(frame => (DateTimeOffset?)frame.Timestamp)
            .FirstOrDefault();
        return new TemporalTransition
        {
            FirstChangeTime = comparison.MessageAppeared ? first : eventTime,
            ReactionMilliseconds = comparison.MessageAppeared && first.HasValue && eventTime.HasValue
                ? (first.Value - eventTime.Value).TotalMilliseconds
                : 0,
            TransitionCount = 1
        };
    }

    private static DateTimeOffset? DetectEventTime(
        IReadOnlyList<CanFrame> referenceFrames,
        IReadOnlyList<CanFrame> actionFrames)
    {
        var consensus = GenericExperimentComparator.BuildConsensus(referenceFrames);
        foreach (var frame in actionFrames.OrderBy(frame => frame.Timestamp))
        {
            if (!consensus.TryGetValue((frame.Id, frame.IsExtended), out var baseline))
            {
                return frame.Timestamp;
            }

            var length = Math.Min(frame.Data.Length, baseline.Bytes.Length);
            for (var index = 0; index < length; index++)
            {
                if (baseline.ByteAgreementPercent[index] >=
                        GenericExperimentComparator.StableAgreementThresholdPercent &&
                    frame.Data[index] != baseline.Bytes[index])
                {
                    return frame.Timestamp;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? DetectEventTimeNear(
        IReadOnlyList<CanFrame> referenceFrames,
        IReadOnlyList<CanFrame> actionFrames,
        DateTimeOffset approximateTime,
        TimeSpan tolerance)
    {
        var nonNegativeTolerance = tolerance < TimeSpan.Zero ? tolerance.Negate() : tolerance;
        var start = approximateTime - nonNegativeTolerance;
        var end = approximateTime + nonNegativeTolerance;
        var searchFrames = actionFrames
            .Where(frame => frame.Timestamp >= start && frame.Timestamp <= end)
            .ToArray();
        return DetectEventTime(referenceFrames, searchFrames);
    }

    private static IReadOnlyList<CanFrame> Comparable(IEnumerable<CanFrame> frames) => frames
        .Where(frame => frame.Protocol == BusProtocol.ClassicalCan &&
                        frame.Direction == CanDirection.Rx &&
                        !frame.IsRemote &&
                        !frame.IsError)
        .OrderBy(frame => frame.Timestamp)
        .ToArray();

    private static int? SingleBitIndex(byte? mask)
    {
        if (!mask.HasValue || mask.Value == 0 || (mask.Value & (mask.Value - 1)) != 0)
        {
            return null;
        }

        for (var bit = 0; bit < 8; bit++)
        {
            if ((mask.Value & (1 << bit)) != 0)
            {
                return bit;
            }
        }

        return null;
    }

    private static string StableKey(GenericCanComparison comparison, int? bitIndex) =>
        $"{comparison.Id:X8}:{(comparison.IsExtended ? 'E' : 'S')}:" +
        $"{comparison.DataIndex?.ToString() ?? "M"}:{bitIndex?.ToString() ?? "B"}";

    private static IReadOnlyList<byte> Collapse(IEnumerable<byte> values)
    {
        var result = new List<byte>();
        foreach (var value in values)
        {
            if (result.Count == 0 || result[^1] != value)
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static byte? Mode(IEnumerable<byte> values)
    {
        var groups = values.GroupBy(value => value).OrderByDescending(group => group.Count()).ToArray();
        return groups.Length == 0 ? null : groups[0].Key;
    }

    private static double? FindStabilization(
        IReadOnlyList<(DateTimeOffset Timestamp, byte Value)> samples,
        byte? stableValue,
        DateTimeOffset? eventTime)
    {
        if (!stableValue.HasValue || !eventTime.HasValue)
        {
            return null;
        }

        for (var index = 0; index < samples.Count; index++)
        {
            if (samples[index].Value == stableValue.Value &&
                samples.Skip(index).All(sample => sample.Value == stableValue.Value))
            {
                return (samples[index].Timestamp - eventTime.Value).TotalMilliseconds;
            }
        }

        return null;
    }

    private static bool ReturnContainsBaseline(
        IReadOnlyList<CanFrame>? returnFrames,
        GenericCanComparison comparison,
        byte? baseline)
    {
        if (returnFrames is null || !comparison.DataIndex.HasValue || !baseline.HasValue)
        {
            return false;
        }

        return returnFrames.Any(frame =>
            frame.Id == comparison.Id &&
            frame.IsExtended == comparison.IsExtended &&
            frame.Data.Length > comparison.DataIndex.Value &&
            frame.Data[comparison.DataIndex.Value] == baseline.Value);
    }

    private static double? CalculateRate(IReadOnlyList<(DateTimeOffset Timestamp, byte Value)> samples)
    {
        if (samples.Count < 2)
        {
            return null;
        }

        var seconds = (samples[^1].Timestamp - samples[0].Timestamp).TotalSeconds;
        return seconds <= 0 ? null : (samples[^1].Value - samples[0].Value) / seconds;
    }

    private static double CalculateMonotonicity(IReadOnlyList<byte> sequence)
    {
        if (sequence.Count < 2)
        {
            return 100;
        }

        var delta = sequence[^1] - sequence[0];
        if (delta == 0)
        {
            return 0;
        }

        var direction = Math.Sign(delta);
        var matching = 0;
        for (var index = 1; index < sequence.Count; index++)
        {
            if (Math.Sign(sequence[index] - sequence[index - 1]) == direction)
            {
                matching++;
            }
        }

        return matching * 100.0 / (sequence.Count - 1);
    }
}
