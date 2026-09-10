using System.Globalization;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Analysis;

public enum AnalogCalibrationQuality
{
    TwoPointOnly,
    Excellent,
    Good,
    Poor
}

public sealed record AnalogCalibrationPoint(
    double RawValue,
    double PhysicalValue,
    string Label = "");

public sealed record AnalogCalibrationResidual(
    double RawValue,
    double PhysicalValue,
    double PredictedValue,
    double Error);

public sealed record AnalogCalibrationResult
{
    public string Unit { get; init; } = string.Empty;
    public double Scale { get; init; }
    public double Offset { get; init; }
    public double RSquared { get; init; }
    public double RootMeanSquareError { get; init; }
    public double MaximumAbsoluteError { get; init; }
    public double PhysicalSpan { get; init; }
    public double RmsePercentOfSpan { get; init; }
    public double MaximumErrorPercentOfSpan { get; init; }
    public AnalogCalibrationQuality Quality { get; init; }
    public IReadOnlyList<AnalogCalibrationPoint> Points { get; init; } = [];
    public IReadOnlyList<AnalogCalibrationResidual> Residuals { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool LinearityValidated =>
        Points.Count >= 3 && Quality is AnalogCalibrationQuality.Excellent or AnalogCalibrationQuality.Good;

    public bool CanApply => Quality != AnalogCalibrationQuality.Poor;
}

/// <summary>
/// Fits user-supplied physical measurements to a decoded raw CAN value:
/// physical = raw * scale + offset.
/// The unit and physical meaning are never inferred automatically.
/// Two points are sufficient to calculate scale/offset but cannot independently validate linearity.
/// </summary>
public static class AnalogPhysicalCalibration
{
    public static IReadOnlyList<AnalogCalibrationPoint> ParsePoints(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var points = new List<AnalogCalibrationPoint>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
        {
            var line = lines[lineNumber].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash].Trim();
            if (line.Length == 0)
                continue;

            var separator = line.IndexOf('=');
            if (separator < 0)
                separator = line.IndexOf(';');
            if (separator < 0)
                separator = line.IndexOf('\t');
            if (separator <= 0 || separator >= line.Length - 1)
                throw new FormatException(
                    $"Строка {lineNumber + 1}: используйте формат raw = physical, например 1000 = 12.5.");

            var rawToken = line[..separator].Trim();
            var physicalToken = line[(separator + 1)..].Trim();
            if (!TryParseFlexibleDouble(rawToken, out var raw) ||
                !TryParseFlexibleDouble(physicalToken, out var physical))
            {
                throw new FormatException(
                    $"Строка {lineNumber + 1}: raw и physical должны быть конечными числами.");
            }

            points.Add(new AnalogCalibrationPoint(raw, physical, $"point-{points.Count + 1}"));
        }

        return points;
    }

    public static AnalogCalibrationResult Fit(
        IReadOnlyList<AnalogCalibrationPoint> points,
        string unit)
    {
        ArgumentNullException.ThrowIfNull(points);
        var normalizedUnit = NormalizeUnit(unit);
        if (points.Count < 2)
            throw new InvalidOperationException("Для калибровки нужны минимум две физически измеренные точки.");

        if (points.Any(point =>
                !double.IsFinite(point.RawValue) || !double.IsFinite(point.PhysicalValue)))
        {
            throw new InvalidOperationException("Calibration points должны содержать только конечные числа.");
        }

        if (points.Select(point => point.RawValue).Distinct().Count() < 2)
            throw new InvalidOperationException("Raw значения calibration points должны содержать минимум два разных значения.");
        if (points.Select(point => point.PhysicalValue).Distinct().Count() < 2)
            throw new InvalidOperationException("Физические значения calibration points должны содержать минимум два разных значения.");

        var xMean = points.Average(point => point.RawValue);
        var yMean = points.Average(point => point.PhysicalValue);
        var sxx = points.Sum(point => Square(point.RawValue - xMean));
        if (sxx <= double.Epsilon)
            throw new InvalidOperationException("Разброс raw значений слишком мал для расчёта scale/offset.");

        var sxy = points.Sum(point =>
            (point.RawValue - xMean) * (point.PhysicalValue - yMean));
        var scale = sxy / sxx;
        var offset = yMean - scale * xMean;
        if (!double.IsFinite(scale) || !double.IsFinite(offset))
            throw new InvalidOperationException("Не удалось получить конечные scale/offset.");

        var residuals = points.Select(point =>
        {
            var predicted = point.RawValue * scale + offset;
            return new AnalogCalibrationResidual(
                point.RawValue,
                point.PhysicalValue,
                predicted,
                predicted - point.PhysicalValue);
        }).ToArray();

        var sse = residuals.Sum(item => Square(item.Error));
        var sst = points.Sum(point => Square(point.PhysicalValue - yMean));
        var rSquared = sst <= double.Epsilon ? 0 : 1.0 - sse / sst;
        var rmse = Math.Sqrt(sse / points.Count);
        var maxError = residuals.Max(item => Math.Abs(item.Error));
        var physicalMinimum = points.Min(point => point.PhysicalValue);
        var physicalMaximum = points.Max(point => point.PhysicalValue);
        var span = physicalMaximum - physicalMinimum;
        if (span <= double.Epsilon)
            throw new InvalidOperationException("Диапазон физических измерений слишком мал для калибровки.");

        var rmsePercent = 100.0 * rmse / span;
        var maxErrorPercent = 100.0 * maxError / span;
        var quality = Classify(points.Count, rSquared, rmsePercent, maxErrorPercent);
        var warnings = BuildWarnings(points.Count, quality, rSquared, rmsePercent, maxErrorPercent);

        return new AnalogCalibrationResult
        {
            Unit = normalizedUnit,
            Scale = scale,
            Offset = offset,
            RSquared = rSquared,
            RootMeanSquareError = rmse,
            MaximumAbsoluteError = maxError,
            PhysicalSpan = span,
            RmsePercentOfSpan = rmsePercent,
            MaximumErrorPercentOfSpan = maxErrorPercent,
            Quality = quality,
            Points = points.ToArray(),
            Residuals = residuals,
            Warnings = warnings
        };
    }

    public static MachineSignal ApplyToSignal(
        MachineSignal signal,
        AnalogCalibrationResult calibration,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(calibration);
        if (!calibration.CanApply)
            throw new InvalidOperationException(
                "Калибровка имеет качество POOR. Исправьте/повторите физические измерения перед записью в Machine Profile.");
        if (calibration.Points.Count < 2)
            throw new InvalidOperationException("Calibration result не содержит двух измеренных точек.");

        var pointText = string.Join(", ", calibration.Points.Select(point =>
            $"{Format(point.RawValue)}->{Format(point.PhysicalValue)} {calibration.Unit}"));
        var description =
            $"Physical calibration from user-supplied measurements; points={calibration.Points.Count}; " +
            $"raw->physical=[{pointText}]; scale={Format(calibration.Scale)}; " +
            $"offset={Format(calibration.Offset)}; unit={calibration.Unit}; " +
            $"R2={Format(calibration.RSquared)}; RMSE={Format(calibration.RootMeanSquareError)} {calibration.Unit}; " +
            $"maxError={Format(calibration.MaximumAbsoluteError)} {calibration.Unit} " +
            $"({Format(calibration.MaximumErrorPercentOfSpan)}% span); quality={calibration.Quality}. " +
            (calibration.Points.Count == 2
                ? "Two-point fit calculates scale/offset but does not independently validate linearity. "
                : "Linearity was evaluated from three or more physical points. ") +
            "Calibration evidence does not by itself prove sensor identity or physical causality.";

        var evidence = new SignalEvidence
        {
            CaptureOrigin = "guided-analog-calibration",
            Kind = EvidenceKind.PhysicalOutputCheck,
            Description = description,
            SourceReference =
                $"analog-calibration:{signal.SignalId:N}:{recordedAt.ToUniversalTime():yyyyMMddTHHmmssfffZ}",
            RecordedAt = recordedAt
        };

        return signal with
        {
            Scale = calibration.Scale,
            Offset = calibration.Offset,
            Unit = calibration.Unit,
            Evidence = signal.Evidence.Concat([evidence]).ToList(),
            UpdatedAt = recordedAt
        };
    }

    private static AnalogCalibrationQuality Classify(
        int pointCount,
        double rSquared,
        double rmsePercent,
        double maxErrorPercent)
    {
        if (pointCount == 2)
            return AnalogCalibrationQuality.TwoPointOnly;
        if (rSquared >= 0.999 && rmsePercent <= 0.5 && maxErrorPercent <= 1.0)
            return AnalogCalibrationQuality.Excellent;
        if (rSquared >= 0.995 && rmsePercent <= 1.5 && maxErrorPercent <= 2.5)
            return AnalogCalibrationQuality.Good;
        return AnalogCalibrationQuality.Poor;
    }

    private static IReadOnlyList<string> BuildWarnings(
        int pointCount,
        AnalogCalibrationQuality quality,
        double rSquared,
        double rmsePercent,
        double maxErrorPercent)
    {
        var warnings = new List<string>
        {
            "Unit и физический смысл введены инженером и не выводятся автоматически из CAN.",
            "Применение scale/offset не меняет Confidence автоматически и не подтверждает причинность."
        };
        if (pointCount == 2)
        {
            warnings.Add(
                "Использованы только две точки: прямая всегда проходит через обе, поэтому линейность датчика не проверена. Рекомендуется минимум 3 точки по рабочему диапазону.");
        }
        else if (quality == AnalogCalibrationQuality.Poor)
        {
            warnings.Add(
                $"Линейность недостаточна: R²={Format(rSquared)}, RMSE={Format(rmsePercent)}% диапазона, max error={Format(maxErrorPercent)}%. Запись в профиль блокируется.");
        }
        return warnings;
    }

    private static string NormalizeUnit(string unit)
    {
        var normalized = (unit ?? string.Empty).Trim();
        if (normalized.Length == 0)
            throw new InvalidOperationException("Введите инженерную единицу физического измерения, например m, deg или bar.");
        if (normalized.Length > 32 || normalized.Any(char.IsControl) || normalized.Contains('\r') || normalized.Contains('\n'))
            throw new InvalidOperationException("Инженерная единица содержит недопустимые символы или слишком длинная.");
        return normalized;
    }

    private static bool TryParseFlexibleDouble(string token, out double value)
    {
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value))
            return true;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
            double.IsFinite(value))
            return true;

        if (token.Contains(',') && !token.Contains('.'))
        {
            var normalized = token.Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                double.IsFinite(value))
                return true;
        }

        value = 0;
        return false;
    }

    private static double Square(double value) => value * value;

    private static string Format(double value) =>
        value.ToString("0.#########", CultureInfo.InvariantCulture);
}
