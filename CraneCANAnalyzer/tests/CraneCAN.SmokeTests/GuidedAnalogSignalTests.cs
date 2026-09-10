using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

internal static class GuidedAnalogSignalTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        FindsRepeatedLittleEndianRamp();
        FindsRepeatedBigEndianRamp();
        RejectsDiscreteAndOscillatingFields();
        OppositeDirectionPreventsHigh();
        CreatesPortableRawCandidateEvidence();
    }

    private static void FindsRepeatedLittleEndianRamp()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddHours(20);
        var runs = Enumerable.Range(1, 3)
            .Select(repeat => BuildRun(
                repeat,
                t0.AddMinutes(repeat),
                0x500,
                false,
                AnalogFieldEncoding.UInt16LittleEndian,
                increasing: true,
                includeReturn: true))
            .ToArray();

        // Same numeric ID in Extended format is static and must never merge with Standard.
        runs = runs.Select(run => run with
        {
            ReferenceFrames = run.ReferenceFrames.Concat(
                StaticFrames(run.ReferenceFrames[0].Timestamp, 0x500, true, 20, 0x33)).ToArray(),
            ActionFrames = run.ActionFrames.Concat(
                StaticFrames(run.ActionFrames[0].Timestamp, 0x500, true, 30, 0x33)).ToArray()
        }).ToArray();

        var result = GuidedAnalogSignalAnalyzer.Analyze("TELESCOPE_OUT", runs);
        var candidate = result.Candidates.Single(item =>
            item.Id == 0x500 &&
            !item.IsExtended &&
            item.StartByte == 0 &&
            item.Encoding == AnalogFieldEncoding.UInt16LittleEndian);

        Check(candidate.Priority == AnalogCandidatePriority.High,
            "Repeated U16 LE ramp was not ranked HIGH.");
        Check(candidate.RepeatabilityCount == 3 && candidate.RepeatCount == 3,
            "Repeated U16 LE ramp repeatability is incorrect.");
        Check(candidate.Direction == AnalogDirection.Increasing &&
              candidate.MedianMonotonicityPercent >= 90 &&
              candidate.MedianAbsoluteTimeCorrelation >= 0.90 &&
              candidate.MedianResponseToNoiseRatio >= 10,
            "U16 LE analog metrics are not strong enough for the synthetic ramp.");
        Check(candidate.ReturnToBaselineCount == 3 &&
              candidate.ReturnObservedCount == 3,
            "Return-to-baseline was not detected in all repeats.");
        Check(candidate.MedianReactionMilliseconds is >= 0 and <= 400,
            "Analog reaction time was not detected near the operator action.");

        var lowByte = result.Candidates.FirstOrDefault(item =>
            item.Id == 0x500 &&
            item.StartByte == 0 &&
            item.Encoding == AnalogFieldEncoding.ByteUnsigned);
        Check(lowByte is null || lowByte.Score < candidate.Score,
            "Wrapped low byte incorrectly outranked the coherent U16 field.");
        Check(result.Candidates.All(item => !(item.Id == 0x500 && item.IsExtended)),
            "Standard/Extended IDs were merged during analog search.");
    }

    private static void FindsRepeatedBigEndianRamp()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddHours(21);
        var runs = Enumerable.Range(1, 3)
            .Select(repeat => BuildRun(
                repeat,
                t0.AddMinutes(repeat),
                0x510,
                false,
                AnalogFieldEncoding.UInt16BigEndian,
                increasing: true,
                includeReturn: false))
            .ToArray();

        var result = GuidedAnalogSignalAnalyzer.Analyze("BOOM_UP", runs);
        var candidate = result.Candidates.Single(item =>
            item.Id == 0x510 &&
            item.StartByte == 0 &&
            item.Encoding == AnalogFieldEncoding.UInt16BigEndian);

        Check(candidate.Priority == AnalogCandidatePriority.High &&
              candidate.Direction == AnalogDirection.Increasing,
            "Repeated U16 BE ramp was not ranked HIGH.");
        Check(candidate.MedianAbsoluteTimeCorrelation >= 0.90,
            "U16 BE time correlation is unexpectedly weak.");
    }

    private static void RejectsDiscreteAndOscillatingFields()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddHours(22);
        var runs = new List<GuidedExperimentRun>();
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var start = t0.AddMinutes(repeat);
            var reference = new List<CanFrame>();
            var action = new List<CanFrame>();

            for (var index = 0; index < 20; index++)
            {
                reference.Add(Frame(start.AddMilliseconds(index * 50), 0x501, false, [0x00]));
                reference.Add(Frame(start.AddMilliseconds(index * 50 + 1), 0x530, false, [100]));
            }

            for (var index = 0; index < 30; index++)
            {
                action.Add(Frame(start.AddSeconds(2).AddMilliseconds(index * 50),
                    0x501, false, [(byte)(index < 15 ? 0 : 255)]));
                var oscillating = (byte)(100 + ((index % 6) - 3) * 5);
                action.Add(Frame(start.AddSeconds(2).AddMilliseconds(index * 50 + 1),
                    0x530, false, [oscillating]));
            }

            runs.Add(new GuidedExperimentRun(
                repeat,
                "CAN1",
                reference,
                action,
                start.AddSeconds(2)));
        }

        var result = GuidedAnalogSignalAnalyzer.Analyze("BUTTON_OR_NOISE", runs);
        Check(result.Candidates.All(item => item.Id != 0x501),
            "Two-state discrete field was incorrectly treated as an analog sensor candidate.");
        Check(result.Candidates.All(item => item.Id != 0x530),
            "Oscillating noise field was incorrectly treated as a sensor-like trend.");
    }

    private static void OppositeDirectionPreventsHigh()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddHours(23);
        var runs = new[]
        {
            BuildRun(1, t0.AddMinutes(1), 0x520, false,
                AnalogFieldEncoding.UInt16LittleEndian, true, false),
            BuildRun(2, t0.AddMinutes(2), 0x520, false,
                AnalogFieldEncoding.UInt16LittleEndian, true, false),
            BuildRun(3, t0.AddMinutes(3), 0x520, false,
                AnalogFieldEncoding.UInt16LittleEndian, false, false)
        };

        var result = GuidedAnalogSignalAnalyzer.Analyze("MIXED_DIRECTION", runs);
        var candidate = result.Candidates.Single(item =>
            item.Id == 0x520 &&
            item.StartByte == 0 &&
            item.Encoding == AnalogFieldEncoding.UInt16LittleEndian);

        Check(candidate.RepeatabilityCount == 2 && candidate.RepeatCount == 3,
            "Opposite-direction repeat did not reduce repeatability to 2/3.");
        Check(candidate.Priority != AnalogCandidatePriority.High,
            "Opposite-direction 2/3 candidate was incorrectly ranked HIGH.");
    }

    private static void CreatesPortableRawCandidateEvidence()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddHours(24);
        var runs = Enumerable.Range(1, 3)
            .Select(repeat => BuildRun(
                repeat,
                t0.AddMinutes(repeat),
                0x540,
                false,
                AnalogFieldEncoding.UInt16LittleEndian,
                true,
                true))
            .ToArray();
        var result = GuidedAnalogSignalAnalyzer.Analyze("LENGTH_MOVE", runs);
        var candidate = result.Candidates.Single(item =>
            item.Id == 0x540 &&
            item.StartByte == 0 &&
            item.Encoding == AnalogFieldEncoding.UInt16LittleEndian);

        var experimentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var recordedAt = DateTimeOffset.Parse("2026-09-10T10:00:00+00:00");
        var signal = GuidedAnalogSignalAnalyzer.CreateMachineSignal(
            candidate,
            experimentId,
            recordedAt);

        Check(signal.Confidence == SignalKnowledgeState.Candidate &&
              !signal.IsSigned &&
              signal.Scale == 1 &&
              signal.Offset == 0 &&
              string.IsNullOrEmpty(signal.Unit),
            "Analog candidate was given unverified engineering semantics.");
        Check(signal.StartByte == 0 &&
              signal.StartBit == 0 &&
              signal.BitLength == 16 &&
              signal.ByteOrder == SignalByteOrder.LittleEndian,
            "U16 LE candidate was mapped incorrectly into MachineSignal.");
        var evidence = signal.Evidence.Single();
        Check(evidence.Kind == EvidenceKind.RepeatedExperiment &&
              evidence.ExperimentId == experimentId &&
              evidence.ExperimentPath is null &&
              evidence.Repeats.Count == 0 &&
              evidence.SourceReference is not null &&
              evidence.SourceReference.Contains(experimentId.ToString("N"), StringComparison.Ordinal),
            "Analog candidate evidence is not portable or lost experiment provenance.");

        var beRuns = Enumerable.Range(1, 3)
            .Select(repeat => BuildRun(
                repeat,
                t0.AddHours(1).AddMinutes(repeat),
                0x541,
                false,
                AnalogFieldEncoding.UInt16BigEndian,
                true,
                false))
            .ToArray();
        var beCandidate = GuidedAnalogSignalAnalyzer.Analyze("ANGLE_MOVE", beRuns)
            .Candidates.Single(item =>
                item.Id == 0x541 &&
                item.StartByte == 0 &&
                item.Encoding == AnalogFieldEncoding.UInt16BigEndian);
        var beSignal = GuidedAnalogSignalAnalyzer.CreateMachineSignal(
            beCandidate,
            experimentId,
            recordedAt);
        Check(beSignal.StartBit == 7 &&
              beSignal.BitLength == 16 &&
              beSignal.ByteOrder == SignalByteOrder.BigEndian,
            "U16 BE candidate was not mapped to established DBC/Motorola start-bit convention.");
    }

    private static GuidedExperimentRun BuildRun(
        int repeat,
        DateTimeOffset start,
        uint id,
        bool extended,
        AnalogFieldEncoding encoding,
        bool increasing,
        bool includeReturn)
    {
        var reference = new List<CanFrame>();
        var action = new List<CanFrame>();
        var returned = new List<CanFrame>();

        const int baseline = 1000;
        for (var index = 0; index < 20; index++)
            reference.Add(ValueFrame(start.AddMilliseconds(index * 50), id, extended, baseline, encoding));

        var actionStart = start.AddSeconds(2);
        for (var index = 0; index < 30; index++)
        {
            var value = increasing
                ? baseline + index * 40
                : baseline - index * 20;
            action.Add(ValueFrame(actionStart.AddMilliseconds(index * 50), id, extended, value, encoding));
        }

        if (includeReturn)
        {
            var returnStart = start.AddSeconds(4);
            for (var index = 0; index < 12; index++)
                returned.Add(ValueFrame(returnStart.AddMilliseconds(index * 50), id, extended, baseline, encoding));
        }

        // A transmit frame must not influence passive analysis.
        action.Add(ValueFrame(actionStart.AddMilliseconds(25), id, extended, 60000, encoding) with
        {
            Direction = CanDirection.Tx
        });

        return new GuidedExperimentRun(
            repeat,
            "CAN1",
            reference,
            action,
            actionStart,
            TimeSpan.FromSeconds(1),
            includeReturn ? returned : null);
    }

    private static IEnumerable<CanFrame> StaticFrames(
        DateTimeOffset start,
        uint id,
        bool extended,
        int count,
        byte value)
    {
        for (var index = 0; index < count; index++)
            yield return Frame(start.AddMilliseconds(index * 50), id, extended, [value, value]);
    }

    private static CanFrame ValueFrame(
        DateTimeOffset timestamp,
        uint id,
        bool extended,
        int value,
        AnalogFieldEncoding encoding)
    {
        var raw = checked((ushort)value);
        var data = encoding == AnalogFieldEncoding.UInt16BigEndian
            ? new[] { (byte)(raw >> 8), (byte)(raw & 0xFF) }
            : new[] { (byte)(raw & 0xFF), (byte)(raw >> 8) };
        return Frame(timestamp, id, extended, data);
    }

    private static CanFrame Frame(
        DateTimeOffset timestamp,
        uint id,
        bool extended,
        byte[] data) =>
        new()
        {
            Timestamp = timestamp,
            Channel = 0,
            Id = id,
            IsExtended = extended,
            Data = data,
            Protocol = BusProtocol.ClassicalCan,
            Direction = CanDirection.Rx
        };

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
