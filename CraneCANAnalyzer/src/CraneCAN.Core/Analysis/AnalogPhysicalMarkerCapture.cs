using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Core.Analysis;

public sealed record AnalogMarkerCaptureOptions
{
    public TimeSpan SamplingWindow { get; init; } = TimeSpan.FromMilliseconds(750);
    public int MinimumSamples { get; init; } = 5;
    public bool RequireRecentFrame { get; init; }
    public DateTimeOffset? ReferenceTime { get; init; }
    public TimeSpan MaximumFrameAge { get; init; } = TimeSpan.FromSeconds(2);
    public double StabilityFractionOfFullScale { get; init; } = 0.001;
    public double MaximumStabilityToleranceRaw { get; init; } = 64;
}

public sealed record AnalogMarkerCaptureResult
{
    public double RawValue { get; init; }
    public double LatestRawValue { get; init; }
    public double MinimumRawValue { get; init; }
    public double MaximumRawValue { get; init; }
    public double RobustSpanRaw { get; init; }
    public double DriftRaw { get; init; }
    public double AllowedStabilityRaw { get; init; }
    public int SampleCount { get; init; }
    public DateTimeOffset FirstSampleTimestamp { get; init; }
    public DateTimeOffset LastSampleTimestamp { get; init; }
    public uint LatestObservedCanId { get; init; }
    public IReadOnlyList<uint> ObservedCanIds { get; init; } = [];
    public int? J1939Pgn { get; init; }
    public int? J1939SourceAddress { get; init; }
    public int? J1939DestinationAddress { get; init; }
    public bool IsStable { get; init; }
    public string StabilityMessage { get; init; } = string.Empty;
}

/// <summary>
/// Passively captures the current raw value of a Machine Profile signal from a
/// snapshot of already-received Classical CAN frames. No transmit path exists.
/// A short trailing window is used so a calibration marker cannot silently use
/// one transient frame while the mechanism is still moving.
/// </summary>
public static class AnalogPhysicalMarkerCapture
{
    public static AnalogMarkerCaptureResult Capture(
        MachineSignal signal,
        IReadOnlyList<CanFrame> frames,
        AnalogMarkerCaptureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(frames);
        options ??= new AnalogMarkerCaptureOptions();
        ValidateOptions(options);
        MachineSignalDecoder.ValidateDefinition(signal);
        var requiredLength = MachineSignalDecoder.RequiredDataLength(signal);
        var isJ1939 = IsJ1939(signal);

        var matching = frames
            .Where(frame => IsUsable(frame, requiredLength))
            .Where(frame => MatchesSignal(signal, frame))
            .OrderBy(frame => frame.Timestamp)
            .ToArray();
        if (matching.Length == 0)
        {
            throw new InvalidOperationException(
                $"В Live/Replay buffer нет пригодных Rx кадров для сигнала «{signal.Name}».");
        }

        var latest = matching[^1];
        if (options.RequireRecentFrame)
        {
            var referenceTime = options.ReferenceTime ?? DateTimeOffset.UtcNow;
            var age = referenceTime - latest.Timestamp;
            if (age > options.MaximumFrameAge)
            {
                throw new InvalidOperationException(
                    $"Последний кадр сигнала «{signal.Name}» старше допустимого интервала: " +
                    $"{age.TotalMilliseconds:0} ms. Проверьте Live CAN и повторите фиксацию.");
            }
        }

        int? sourceAddress = null;
        int? destinationAddress = null;
        int? pgn = null;
        IEnumerable<CanFrame> currentSender = matching;
        if (isJ1939)
        {
            var latestIdentifier = J1939IdentifierCodec.Decode(latest.Id);
            sourceAddress = latestIdentifier.SourceAddress;
            destinationAddress = latestIdentifier.DestinationAddress;
            pgn = latestIdentifier.Pgn;
            currentSender = matching.Where(frame =>
            {
                var identifier = J1939IdentifierCodec.Decode(frame.Id);
                return identifier.SourceAddress == latestIdentifier.SourceAddress;
            });
        }
        else
        {
            currentSender = matching.Where(frame => frame.Id == latest.Id);
        }

        var windowStart = latest.Timestamp - options.SamplingWindow;
        var window = currentSender
            .Where(frame => frame.Timestamp >= windowStart && frame.Timestamp <= latest.Timestamp)
            .OrderBy(frame => frame.Timestamp)
            .ToArray();
        if (window.Length < options.MinimumSamples)
        {
            throw new InvalidOperationException(
                $"Недостаточно свежих кадров для фиксации «{signal.Name}»: " +
                $"{window.Length}/{options.MinimumSamples} за {options.SamplingWindow.TotalMilliseconds:0} ms. " +
                "Удерживайте положение и повторите.");
        }

        var samples = window.Select(frame => new RawSample(
            frame.Timestamp,
            frame.Id,
            DecodeRaw(signal, frame))).ToArray();
        var values = samples.Select(sample => sample.Value).ToArray();
        var median = Median(values);
        var robustMinimum = Percentile(values, 0.05);
        var robustMaximum = Percentile(values, 0.95);
        var robustSpan = robustMaximum - robustMinimum;
        var half = Math.Max(1, values.Length / 2);
        var firstMedian = Median(values.Take(half));
        var lastMedian = Median(values.Skip(values.Length - half));
        var drift = Math.Abs(lastMedian - firstMedian);
        var fullScale = Math.Pow(2.0, signal.BitLength) - 1.0;
        var allowed = Math.Clamp(
            fullScale * options.StabilityFractionOfFullScale,
            1.0,
            options.MaximumStabilityToleranceRaw);
        var stable = robustSpan <= allowed && drift <= allowed;
        var message = stable
            ? $"STABLE: span={robustSpan:0.###} raw, drift={drift:0.###} raw, limit={allowed:0.###} raw."
            : $"UNSTABLE: span={robustSpan:0.###} raw, drift={drift:0.###} raw, limit={allowed:0.###} raw. " +
              "Остановите движение, удерживайте физическое положение и повторите фиксацию.";

        return new AnalogMarkerCaptureResult
        {
            RawValue = median,
            LatestRawValue = samples[^1].Value,
            MinimumRawValue = values.Min(),
            MaximumRawValue = values.Max(),
            RobustSpanRaw = robustSpan,
            DriftRaw = drift,
            AllowedStabilityRaw = allowed,
            SampleCount = samples.Length,
            FirstSampleTimestamp = samples[0].Timestamp,
            LastSampleTimestamp = samples[^1].Timestamp,
            LatestObservedCanId = latest.Id,
            ObservedCanIds = samples.Select(sample => sample.CanId).Distinct().OrderBy(id => id).ToArray(),
            J1939Pgn = pgn,
            J1939SourceAddress = sourceAddress,
            J1939DestinationAddress = destinationAddress,
            IsStable = stable,
            StabilityMessage = message
        };
    }

    private static bool MatchesSignal(MachineSignal signal, CanFrame frame)
    {
        if (IsJ1939(signal))
            return J1939SignalDecoder.MatchesIdentifier(signal, frame.Id, frame.IsExtended);
        return frame.Id == signal.CanId && frame.IsExtended == signal.IsExtended;
    }

    private static bool IsJ1939(MachineSignal signal) =>
        signal.IsExtended &&
        signal.J1939Pgn.HasValue &&
        string.Equals(signal.Protocol, "J1939", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(CanFrame frame, int requiredLength) =>
        frame.Protocol == BusProtocol.ClassicalCan &&
        frame.Direction == CanDirection.Rx &&
        !frame.IsRemote &&
        !frame.IsError &&
        frame.Data.Length >= requiredLength &&
        frame.Data.Length <= 8;

    private static double DecodeRaw(MachineSignal signal, CanFrame frame)
    {
        var decoded = MachineSignalDecoder.Decode(signal, frame.Data);
        return signal.IsSigned
            ? decoded.RawSigned!.Value
            : decoded.RawUnsigned;
    }

    private static void ValidateOptions(AnalogMarkerCaptureOptions options)
    {
        if (options.SamplingWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.SamplingWindow));
        if (options.MinimumSamples < 2)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumSamples));
        if (options.MaximumFrameAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumFrameAge));
        if (!double.IsFinite(options.StabilityFractionOfFullScale) ||
            options.StabilityFractionOfFullScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.StabilityFractionOfFullScale));
        if (!double.IsFinite(options.MaximumStabilityToleranceRaw) ||
            options.MaximumStabilityToleranceRaw < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumStabilityToleranceRaw));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            throw new InvalidOperationException("Нельзя вычислить median пустого набора.");
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            throw new InvalidOperationException("Нельзя вычислить percentile пустого набора.");
        if (sorted.Length == 1)
            return sorted[0];
        var position = (sorted.Length - 1) * Math.Clamp(percentile, 0, 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private sealed record RawSample(DateTimeOffset Timestamp, uint CanId, double Value);
}
