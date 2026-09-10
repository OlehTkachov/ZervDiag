using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

internal static class AnalogCalibrationValidationTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        PerfectIndependentPointsPassWithoutRefit();
        ModerateErrorProducesCaution();
        LargeErrorFails();
        RequiresThreeDistinctPhysicalPositions();
        PassEvidencePreservesCalibrationAndConfidence();
        CautionAndFailCannotRecordEvidence();
        StaleResultCannotBeAppliedAfterCalibrationChange();
        MissingUnitIsRejected();
    }

    private static void PerfectIndependentPointsPassWithoutRefit()
    {
        var signal = CalibratedSignal();
        var result = AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.0, "independent-low"),
            new AnalogCalibrationValidationPoint(1500, 17.0, "independent-mid"),
            new AnalogCalibrationValidationPoint(2000, 22.0, "independent-high")
        ]);

        Check(result.Quality == AnalogCalibrationValidationQuality.Pass,
            "Perfect independent validation points did not produce PASS.");
        Check(result.EvaluatedScale == signal.Scale && result.EvaluatedOffset == signal.Offset,
            "Validation changed or refitted Scale/Offset.");
        Check(result.RootMeanSquareError == 0 && result.MaximumAbsoluteError == 0 && result.MeanError == 0,
            "Perfect validation should have zero error metrics.");
        Check(result.Residuals.Select(item => item.PredictedPhysicalValue).SequenceEqual(new[] { 12.0, 17.0, 22.0 }),
            "Validation predictions do not use the existing Scale/Offset formula.");
        Check(result.CanRecordEvidence,
            "PASS with three independent points should permit evidence recording.");
    }

    private static void ModerateErrorProducesCaution()
    {
        var result = AnalogCalibrationValidation.Evaluate(CalibratedSignal(),
        [
            new AnalogCalibrationValidationPoint(1000, 12.1),
            new AnalogCalibrationValidationPoint(1500, 17.1),
            new AnalogCalibrationValidationPoint(2000, 21.7)
        ]);

        Check(result.Quality == AnalogCalibrationValidationQuality.Caution,
            "Moderate validation error should produce CAUTION.");
        Check(!result.CanRecordEvidence,
            "CAUTION must not be written as passing validation evidence.");
        Check(result.MaximumErrorPercentOfSpan > 2.5 && result.MaximumErrorPercentOfSpan <= 5.0,
            "CAUTION max-error percentage is outside the intended band.");
    }

    private static void LargeErrorFails()
    {
        var result = AnalogCalibrationValidation.Evaluate(CalibratedSignal(),
        [
            new AnalogCalibrationValidationPoint(1000, 13.0),
            new AnalogCalibrationValidationPoint(1500, 17.0),
            new AnalogCalibrationValidationPoint(2000, 21.0)
        ]);

        Check(result.Quality == AnalogCalibrationValidationQuality.Fail,
            "Large validation mismatch should produce FAIL.");
        Check(!result.CanRecordEvidence,
            "FAIL must not permit validation evidence recording.");
    }

    private static void RequiresThreeDistinctPhysicalPositions()
    {
        var signal = CalibratedSignal();
        ExpectThrows<InvalidOperationException>(() => AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.0),
            new AnalogCalibrationValidationPoint(1500, 17.0)
        ]), "Validation accepted fewer than three points.");

        ExpectThrows<InvalidOperationException>(() => AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.0),
            new AnalogCalibrationValidationPoint(1500, 12.0),
            new AnalogCalibrationValidationPoint(2000, 22.0)
        ]), "Validation accepted fewer than three distinct physical positions.");

        ExpectThrows<InvalidOperationException>(() => AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.0),
            new AnalogCalibrationValidationPoint(1000, 17.0),
            new AnalogCalibrationValidationPoint(2000, 22.0)
        ]), "Validation accepted fewer than three distinct raw values.");
    }

    private static void PassEvidencePreservesCalibrationAndConfidence()
    {
        var signal = CalibratedSignal() with
        {
            Confidence = SignalKnowledgeState.Candidate,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.RepeatedExperiment,
                    Description = "existing"
                }
            ]
        };
        var result = AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.0, Source: "PCAN LIVE"),
            new AnalogCalibrationValidationPoint(1500, 17.0, Source: "PCAN LIVE"),
            new AnalogCalibrationValidationPoint(2000, 22.0, Source: "PCAN LIVE")
        ]);
        var recordedAt = DateTimeOffset.Parse("2026-09-10T12:15:00+00:00");
        var updated = AnalogCalibrationValidation.AddEvidence(signal, result, recordedAt);

        Check(updated.Scale == signal.Scale && updated.Offset == signal.Offset && updated.Unit == signal.Unit,
            "Recording validation evidence changed the calibration.");
        Check(updated.Confidence == signal.Confidence,
            "Recording validation evidence changed Confidence automatically.");
        Check(updated.Evidence.Count == signal.Evidence.Count + 1,
            "PASS validation evidence was not appended exactly once.");
        var evidence = updated.Evidence[^1];
        Check(evidence.Kind == EvidenceKind.PhysicalOutputCheck &&
              evidence.CaptureOrigin == "analog-calibration-validation",
            "Validation evidence metadata is incorrect.");
        Check(evidence.Description.Contains("without refitting", StringComparison.Ordinal) &&
              evidence.Description.Contains("does not change Confidence automatically", StringComparison.Ordinal),
            "Validation evidence does not preserve its safety/interpretation constraints.");
        Check(updated.UpdatedAt == recordedAt,
            "Validation evidence did not update signal timestamp.");
    }

    private static void CautionAndFailCannotRecordEvidence()
    {
        var signal = CalibratedSignal();
        var caution = AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.1),
            new AnalogCalibrationValidationPoint(1500, 17.1),
            new AnalogCalibrationValidationPoint(2000, 21.7)
        ]);
        var fail = AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 13.0),
            new AnalogCalibrationValidationPoint(1500, 17.0),
            new AnalogCalibrationValidationPoint(2000, 21.0)
        ]);

        ExpectThrows<InvalidOperationException>(() =>
            AnalogCalibrationValidation.AddEvidence(signal, caution, DateTimeOffset.UtcNow),
            "CAUTION validation was incorrectly written as evidence.");
        ExpectThrows<InvalidOperationException>(() =>
            AnalogCalibrationValidation.AddEvidence(signal, fail, DateTimeOffset.UtcNow),
            "FAIL validation was incorrectly written as evidence.");
    }

    private static void StaleResultCannotBeAppliedAfterCalibrationChange()
    {
        var signal = CalibratedSignal();
        var result = AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.0),
            new AnalogCalibrationValidationPoint(1500, 17.0),
            new AnalogCalibrationValidationPoint(2000, 22.0)
        ]);
        var changed = signal with { Scale = 0.02 };

        ExpectThrows<InvalidOperationException>(() =>
            AnalogCalibrationValidation.AddEvidence(changed, result, DateTimeOffset.UtcNow),
            "A stale validation result was accepted after Scale changed.");
    }

    private static void MissingUnitIsRejected()
    {
        var signal = CalibratedSignal() with { Unit = string.Empty };
        ExpectThrows<InvalidOperationException>(() => AnalogCalibrationValidation.Evaluate(signal,
        [
            new AnalogCalibrationValidationPoint(1000, 12.0),
            new AnalogCalibrationValidationPoint(1500, 17.0),
            new AnalogCalibrationValidationPoint(2000, 22.0)
        ]), "Validation accepted a signal without engineering Unit.");
    }

    private static MachineSignal CalibratedSignal() => new()
    {
        SignalId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "Boom length",
        CanId = 0x321,
        IsExtended = false,
        StartByte = 0,
        StartBit = 0,
        BitLength = 16,
        ByteOrder = SignalByteOrder.LittleEndian,
        IsSigned = false,
        Scale = 0.01,
        Offset = 2.0,
        Unit = "m",
        Confidence = SignalKnowledgeState.Candidate
    };

    private static void ExpectThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
