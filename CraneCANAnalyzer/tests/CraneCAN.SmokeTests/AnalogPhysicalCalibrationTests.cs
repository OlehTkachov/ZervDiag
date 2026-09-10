using System.Globalization;
using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

internal static class AnalogPhysicalCalibrationTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        ParsesDotAndCommaMeasurements();
        CalculatesTwoPointScaleAndOffsetWithoutClaimingLinearity();
        ValidatesThreePointLinearCalibration();
        BlocksPoorNonlinearCalibration();
        RejectsInvalidCalibrationInputs();
        AppliesCalibrationWithoutAutoPromotingConfidence();
    }

    private static void ParsesDotAndCommaMeasurements()
    {
        var points = AnalogPhysicalCalibration.ParsePoints(
            "# raw = length\n1000 = 12,5\n1300 = 14.0\n1700;16,0\n");

        Check(points.Count == 3, "Calibration parser did not read all physical points.");
        Check(Close(points[0].RawValue, 1000) && Close(points[0].PhysicalValue, 12.5),
            "Calibration parser did not accept decimal comma.");
        Check(Close(points[2].RawValue, 1700) && Close(points[2].PhysicalValue, 16.0),
            "Calibration parser did not accept semicolon separator.");
    }

    private static void CalculatesTwoPointScaleAndOffsetWithoutClaimingLinearity()
    {
        var result = AnalogPhysicalCalibration.Fit(
            [
                new AnalogCalibrationPoint(1000, 12.5),
                new AnalogCalibrationPoint(2000, 17.5)
            ],
            "m");

        Check(Close(result.Scale, 0.005) && Close(result.Offset, 7.5),
            "Two-point calibration produced incorrect scale/offset.");
        Check(result.Quality == AnalogCalibrationQuality.TwoPointOnly,
            "Two-point calibration was incorrectly treated as a validated multi-point fit.");
        Check(!result.LinearityValidated && result.CanApply,
            "Two-point calibration should be applicable but must not claim linearity validation.");
        Check(result.Warnings.Any(warning => warning.Contains("две точки", StringComparison.OrdinalIgnoreCase)),
            "Two-point linearity warning is missing.");
    }

    private static void ValidatesThreePointLinearCalibration()
    {
        var result = AnalogPhysicalCalibration.Fit(
            [
                new AnalogCalibrationPoint(1000, 12.5),
                new AnalogCalibrationPoint(1300, 14.0),
                new AnalogCalibrationPoint(1700, 16.0),
                new AnalogCalibrationPoint(2000, 17.5)
            ],
            "m");

        Check(Close(result.Scale, 0.005) && Close(result.Offset, 7.5),
            "Multi-point calibration produced incorrect scale/offset.");
        Check(result.Quality == AnalogCalibrationQuality.Excellent && result.LinearityValidated,
            "Exact multi-point linear calibration was not classified EXCELLENT.");
        Check(result.RSquared > 0.999999 &&
              result.RootMeanSquareError < 1e-9 &&
              result.MaximumAbsoluteError < 1e-9,
            "Linear fit error metrics are incorrect for exact calibration data.");
    }

    private static void BlocksPoorNonlinearCalibration()
    {
        var result = AnalogPhysicalCalibration.Fit(
            [
                new AnalogCalibrationPoint(1000, 12.5),
                new AnalogCalibrationPoint(1300, 14.0),
                new AnalogCalibrationPoint(1700, 21.0),
                new AnalogCalibrationPoint(2000, 17.5)
            ],
            "m");

        Check(result.Quality == AnalogCalibrationQuality.Poor && !result.CanApply,
            "Strongly nonlinear calibration was not blocked.");
        Check(result.Warnings.Any(warning => warning.Contains("Запись в профиль блокируется", StringComparison.Ordinal)),
            "Poor-fit blocking warning is missing.");
    }

    private static void RejectsInvalidCalibrationInputs()
    {
        CheckThrows<InvalidOperationException>(() =>
            AnalogPhysicalCalibration.Fit(
                [new AnalogCalibrationPoint(1000, 12.5)], "m"),
            "Single-point calibration was accepted.");

        CheckThrows<InvalidOperationException>(() =>
            AnalogPhysicalCalibration.Fit(
                [
                    new AnalogCalibrationPoint(1000, 12.5),
                    new AnalogCalibrationPoint(1000, 14.0)
                ],
                "m"),
            "Duplicate-only raw values were accepted.");

        CheckThrows<InvalidOperationException>(() =>
            AnalogPhysicalCalibration.Fit(
                [
                    new AnalogCalibrationPoint(1000, 12.5),
                    new AnalogCalibrationPoint(1300, 14.0)
                ],
                ""),
            "Calibration without engineering unit was accepted.");

        CheckThrows<FormatException>(() =>
            AnalogPhysicalCalibration.ParsePoints("1000 12.5"),
            "Malformed calibration line was accepted.");
    }

    private static void AppliesCalibrationWithoutAutoPromotingConfidence()
    {
        var signalId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var recordedAt = DateTimeOffset.Parse(
            "2026-09-10T11:30:00+00:00",
            CultureInfo.InvariantCulture);
        var original = new MachineSignal
        {
            SignalId = signalId,
            Name = "HYDAC raw length candidate",
            CanId = 0x500,
            IsExtended = false,
            StartByte = 0,
            StartBit = 0,
            BitLength = 16,
            ByteOrder = SignalByteOrder.LittleEndian,
            IsSigned = false,
            Scale = 1,
            Offset = 0,
            Unit = string.Empty,
            Confidence = SignalKnowledgeState.Candidate,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.RepeatedExperiment,
                    CaptureOrigin = "guided-analog-search",
                    Description = "raw candidate",
                    SourceReference = "guided-analog:test"
                }
            ],
            Source = "Guided Analog Signal Search"
        };

        var calibration = AnalogPhysicalCalibration.Fit(
            [
                new AnalogCalibrationPoint(1000, 12.5),
                new AnalogCalibrationPoint(1300, 14.0),
                new AnalogCalibrationPoint(1700, 16.0)
            ],
            "m");
        var updated = AnalogPhysicalCalibration.ApplyToSignal(original, calibration, recordedAt);

        Check(updated.SignalId == original.SignalId &&
              updated.CanId == original.CanId &&
              updated.StartByte == original.StartByte &&
              updated.StartBit == original.StartBit &&
              updated.BitLength == original.BitLength &&
              updated.ByteOrder == original.ByteOrder &&
              updated.IsSigned == original.IsSigned,
            "Calibration changed signal identity/bit layout.");
        Check(Close(updated.Scale, 0.005) && Close(updated.Offset, 7.5) && updated.Unit == "m",
            "Calibration was not written to MachineSignal.");
        Check(updated.Confidence == SignalKnowledgeState.Candidate,
            "Physical calibration incorrectly auto-promoted signal confidence.");
        Check(updated.Evidence.Count == original.Evidence.Count + 1,
            "Physical calibration evidence was not appended.");

        var evidence = updated.Evidence[^1];
        Check(evidence.Kind == EvidenceKind.PhysicalOutputCheck &&
              evidence.CaptureOrigin == "guided-analog-calibration" &&
              evidence.SourceReference is not null &&
              evidence.SourceReference.Contains(signalId.ToString("N"), StringComparison.Ordinal) &&
              evidence.Description.Contains("scale=0.005", StringComparison.Ordinal) &&
              evidence.Description.Contains("unit=m", StringComparison.Ordinal),
            "Physical calibration evidence lost provenance or engineering parameters.");
        Check(!evidence.Description.Contains("C:\\", StringComparison.OrdinalIgnoreCase) &&
              !evidence.Description.Contains("/home/", StringComparison.OrdinalIgnoreCase),
            "Physical calibration evidence leaked an absolute local path.");

        var poor = AnalogPhysicalCalibration.Fit(
            [
                new AnalogCalibrationPoint(0, 0),
                new AnalogCalibrationPoint(10, 10),
                new AnalogCalibrationPoint(20, 100)
            ],
            "bar");
        CheckThrows<InvalidOperationException>(() =>
            AnalogPhysicalCalibration.ApplyToSignal(original, poor, recordedAt),
            "POOR calibration was written into Machine Profile.");
    }

    private static bool Close(double actual, double expected, double tolerance = 1e-9) =>
        Math.Abs(actual - expected) <= tolerance;

    private static void CheckThrows<TException>(Action action, string message)
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
