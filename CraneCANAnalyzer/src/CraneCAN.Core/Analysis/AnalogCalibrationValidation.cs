using System.Globalization;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Analysis;

public enum AnalogCalibrationValidationQuality
{
    Pass,
    Caution,
    Fail
}

public sealed record AnalogCalibrationValidationPoint(
    double RawValue,
    double MeasuredPhysicalValue,
    string Label = "",
    string Source = "",
    DateTimeOffset? CapturedAt = null);

public sealed record AnalogCalibrationValidationResidual(
    double RawValue,
    double MeasuredPhysicalValue,
    double PredictedPhysicalValue,
    double Error);

public sealed record AnalogCalibrationValidationResult
{
    public Guid SignalId { get; init; }
    public double EvaluatedScale { get; init; }
    public double EvaluatedOffset { get; init; }
    public string Unit { get; init; } = string.Empty;
    public IReadOnlyList<AnalogCalibrationValidationPoint> Points { get; init; } = [];
    public IReadOnlyList<AnalogCalibrationValidationResidual> Residuals { get; init; } = [];
    public double MeanError { get; init; }
    public double RootMeanSquareError { get; init; }
    public double MaximumAbsoluteError { get; init; }
    public double PhysicalSpan { get; init; }
    public double RmsePercentOfSpan { get; init; }
    public double MaximumErrorPercentOfSpan { get; init; }
    public AnalogCalibrationValidationQuality Quality { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool CanRecordEvidence =>
        Points.Count >= 3 && Quality == AnalogCalibrationValidationQuality.Pass;
}

/// <summary>
/// Validates an already-calibrated MachineSignal against independent physical
/// measurements. The existing Scale/Offset are never refitted or modified.
/// </summary>
public static class AnalogCalibrationValidation
{
    public const int MinimumValidationPoints = 3;

    public static AnalogCalibrationValidationResult Evaluate(
        MachineSignal signal,
        IReadOnlyList<AnalogCalibrationValidationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(points);
        MachineSignalDecoder.ValidateDefinition(signal);

        var unit = (signal.Unit ?? string.Empty).Trim();
        if (unit.Length == 0)
        {
            throw new InvalidOperationException(
                "У сигнала не задана Unit. Сначала выполните физическую калибровку Scale/Offset/Unit.");
        }

        if (points.Count < MinimumValidationPoints)
        {
            throw new InvalidOperationException(
                $"Для независимой проверки нужны минимум {MinimumValidationPoints} физические точки.");
        }

        if (points.Any(point =>
                !double.IsFinite(point.RawValue) ||
                !double.IsFinite(point.MeasuredPhysicalValue)))
        {
            throw new InvalidOperationException(
                "Validation points должны содержать только конечные raw и physical значения.");
        }

        if (points.Select(point => point.RawValue).Distinct().Count() < MinimumValidationPoints)
        {
            throw new InvalidOperationException(
                "Для проверки диапазона нужны минимум три разных raw значения.");
        }

        if (points.Select(point => point.MeasuredPhysicalValue).Distinct().Count() < MinimumValidationPoints)
        {
            throw new InvalidOperationException(
                "Для проверки диапазона нужны минимум три разных физических значения.");
        }

        var measuredMinimum = points.Min(point => point.MeasuredPhysicalValue);
        var measuredMaximum = points.Max(point => point.MeasuredPhysicalValue);
        var span = measuredMaximum - measuredMinimum;
        if (!double.IsFinite(span) || span <= double.Epsilon)
        {
            throw new InvalidOperationException(
                "Диапазон независимых физических точек слишком мал для проверки калибровки.");
        }

        var residuals = points.Select(point =>
        {
            var predicted = point.RawValue * signal.Scale + signal.Offset;
            if (!double.IsFinite(predicted))
            {
                throw new OverflowException(
                    $"Расчёт physical для raw={Format(point.RawValue)} вышел за диапазон double.");
            }

            return new AnalogCalibrationValidationResidual(
                point.RawValue,
                point.MeasuredPhysicalValue,
                predicted,
                predicted - point.MeasuredPhysicalValue);
        }).ToArray();

        var meanError = residuals.Average(item => item.Error);
        var rmse = Math.Sqrt(residuals.Average(item => item.Error * item.Error));
        var maximumError = residuals.Max(item => Math.Abs(item.Error));
        var rmsePercent = 100.0 * rmse / span;
        var maximumErrorPercent = 100.0 * maximumError / span;
        var quality = Classify(rmsePercent, maximumErrorPercent);
        var warnings = BuildWarnings(quality, span, unit, rmsePercent, maximumErrorPercent);

        return new AnalogCalibrationValidationResult
        {
            SignalId = signal.SignalId,
            EvaluatedScale = signal.Scale,
            EvaluatedOffset = signal.Offset,
            Unit = unit,
            Points = points.ToArray(),
            Residuals = residuals,
            MeanError = meanError,
            RootMeanSquareError = rmse,
            MaximumAbsoluteError = maximumError,
            PhysicalSpan = span,
            RmsePercentOfSpan = rmsePercent,
            MaximumErrorPercentOfSpan = maximumErrorPercent,
            Quality = quality,
            Warnings = warnings
        };
    }

    public static MachineSignal AddEvidence(
        MachineSignal signal,
        AnalogCalibrationValidationResult validation,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.CanRecordEvidence)
        {
            throw new InvalidOperationException(
                "Validation evidence записывается только при результате PASS с минимум тремя независимыми точками.");
        }

        if (validation.SignalId != signal.SignalId)
        {
            throw new InvalidOperationException(
                "Validation result относится к другому Machine Profile signal.");
        }

        if (validation.EvaluatedScale != signal.Scale ||
            validation.EvaluatedOffset != signal.Offset ||
            !string.Equals(validation.Unit, (signal.Unit ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Scale/Offset/Unit сигнала изменились после проверки. Выполните Validation Run повторно.");
        }

        var pointText = string.Join(", ", validation.Residuals.Select(item =>
            $"raw={Format(item.RawValue)}; measured={Format(item.MeasuredPhysicalValue)}; " +
            $"predicted={Format(item.PredictedPhysicalValue)}; error={FormatSigned(item.Error)}"));
        var description =
            $"Independent physical validation PASS; points={validation.Points.Count}; " +
            $"scale={Format(signal.Scale)}; offset={Format(signal.Offset)}; unit={validation.Unit}; " +
            $"meanError={FormatSigned(validation.MeanError)} {validation.Unit}; " +
            $"RMSE={Format(validation.RootMeanSquareError)} {validation.Unit} " +
            $"({Format(validation.RmsePercentOfSpan)}% span); " +
            $"maxError={Format(validation.MaximumAbsoluteError)} {validation.Unit} " +
            $"({Format(validation.MaximumErrorPercentOfSpan)}% span); " +
            $"validationSpan={Format(validation.PhysicalSpan)} {validation.Unit}; points=[{pointText}]. " +
            "Existing Scale/Offset were evaluated without refitting. Operator is responsible for using points independent of the calibration fit. " +
            "Validation evidence does not by itself prove sensor identity or physical causality and does not change Confidence automatically.";

        var evidence = new SignalEvidence
        {
            CaptureOrigin = "analog-calibration-validation",
            Kind = EvidenceKind.PhysicalOutputCheck,
            Description = description,
            SourceReference =
                $"analog-validation:{signal.SignalId:N}:{recordedAt.ToUniversalTime():yyyyMMddTHHmmssfffZ}",
            RecordedAt = recordedAt
        };

        return signal with
        {
            Evidence = signal.Evidence.Concat([evidence]).ToList(),
            UpdatedAt = recordedAt
        };
    }

    private static AnalogCalibrationValidationQuality Classify(
        double rmsePercent,
        double maximumErrorPercent)
    {
        if (rmsePercent <= 1.5 && maximumErrorPercent <= 2.5)
            return AnalogCalibrationValidationQuality.Pass;
        if (rmsePercent <= 3.0 && maximumErrorPercent <= 5.0)
            return AnalogCalibrationValidationQuality.Caution;
        return AnalogCalibrationValidationQuality.Fail;
    }

    private static IReadOnlyList<string> BuildWarnings(
        AnalogCalibrationValidationQuality quality,
        double span,
        string unit,
        double rmsePercent,
        double maximumErrorPercent)
    {
        var warnings = new List<string>
        {
            "Validation Run не пересчитывает Scale/Offset: проверяется именно уже сохранённая в сигнале калибровка.",
            "Используйте физические точки, которые не участвовали в исходной калибровке, и распределите их по рабочему диапазону.",
            $"Результат относится только к проверенному диапазону {Format(span)} {unit}; экстраполяция за него отдельно не подтверждена.",
            "Confidence автоматически не изменяется; PASS не доказывает причинность или идентичность датчика."
        };

        if (quality == AnalogCalibrationValidationQuality.Caution)
        {
            warnings.Add(
                $"CAUTION: RMSE={Format(rmsePercent)}% диапазона, max error={Format(maximumErrorPercent)}%. " +
                "Проверьте физические измерения и повторите независимый Validation Run; evidence не записывается.");
        }
        else if (quality == AnalogCalibrationValidationQuality.Fail)
        {
            warnings.Add(
                $"FAIL: RMSE={Format(rmsePercent)}% диапазона, max error={Format(maximumErrorPercent)}%. " +
                "Не используйте эту калибровку как подтверждённую; evidence не записывается.");
        }

        return warnings;
    }

    private static string Format(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string FormatSigned(double value) =>
        value.ToString(value >= 0 ? "+0.#########" : "0.#########", CultureInfo.InvariantCulture);
}
