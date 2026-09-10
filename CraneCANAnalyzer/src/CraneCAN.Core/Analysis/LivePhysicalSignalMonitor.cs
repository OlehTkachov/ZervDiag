using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Core.Analysis;

public enum LivePhysicalSignalState
{
    NoData,
    Fresh,
    Stale
}

public sealed record LivePhysicalSignalMonitorOptions
{
    public TimeSpan StatisticsWindow { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MinimumRateSpan { get; init; } = TimeSpan.FromMilliseconds(200);
}

public sealed record LivePhysicalSignalReading
{
    public Guid SignalId { get; init; }
    public string SignalName { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public LivePhysicalSignalState State { get; init; }
    public double? CurrentRaw { get; init; }
    public double? CurrentPhysical { get; init; }
    public double? MinimumPhysical { get; init; }
    public double? MaximumPhysical { get; init; }
    public double? RatePerSecond { get; init; }
    public int SampleCount { get; init; }
    public DateTimeOffset? LatestTimestamp { get; init; }
    public TimeSpan? Age { get; init; }
    public uint? LatestObservedCanId { get; init; }
    public IReadOnlyList<uint> ObservedCanIds { get; init; } = [];
    public int? J1939Pgn { get; init; }
    public int? J1939SourceAddress { get; init; }
    public int? J1939DestinationAddress { get; init; }
    public int MatchingFramesWithShortDlc { get; init; }
    public string StatusMessage { get; init; } = string.Empty;
}

/// <summary>
/// Read-only engineering-value monitor over a snapshot of already received
/// Classical CAN frames. It never opens a CAN channel and has no transmit path.
/// For J1939, PGN matching may accept a changed source address, but statistics
/// are calculated only from the source address of the latest matching frame.
/// </summary>
public static class LivePhysicalSignalMonitor
{
    public static LivePhysicalSignalReading Evaluate(
        MachineSignal signal,
        IReadOnlyList<CanFrame> frames,
        DateTimeOffset referenceTime,
        LivePhysicalSignalMonitorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(frames);
        options ??= new LivePhysicalSignalMonitorOptions();
        ValidateOptions(options);
        MachineSignalDecoder.ValidateDefinition(signal);

        var unit = (signal.Unit ?? string.Empty).Trim();
        if (unit.Length == 0)
            throw new InvalidOperationException(
                $"У сигнала «{signal.Name}» не задана Unit. Live Physical Monitor показывает только инженерные значения.");

        var requiredLength = MachineSignalDecoder.RequiredDataLength(signal);
        var identifierMatches = frames
            .Where(IsBaseReceiveDataFrame)
            .Where(frame => MatchesSignal(signal, frame))
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

        if (identifierMatches.Length == 0)
        {
            return NoData(
                signal,
                unit,
                "Нет принятых Rx data frames для выбранного CAN signal.",
                0);
        }

        var shortDlcCount = identifierMatches.Count(frame => frame.Data.Length < requiredLength);
        var usable = identifierMatches
            .Where(frame => frame.Data.Length >= requiredLength && frame.Data.Length <= 8)
            .ToArray();

        if (usable.Length == 0)
        {
            return NoData(
                signal,
                unit,
                $"Есть matching frames, но DATA слишком короткий: требуется минимум {requiredLength} байт.",
                shortDlcCount);
        }

        var latest = usable[^1];
        var isJ1939 = IsJ1939(signal);
        IEnumerable<CanFrame> senderFrames = usable;
        int? pgn = null;
        int? sourceAddress = null;
        int? destinationAddress = null;

        if (isJ1939)
        {
            var latestIdentifier = J1939IdentifierCodec.Decode(latest.Id);
            pgn = latestIdentifier.Pgn;
            sourceAddress = latestIdentifier.SourceAddress;
            destinationAddress = latestIdentifier.DestinationAddress;
            senderFrames = usable.Where(frame =>
            {
                var identifier = J1939IdentifierCodec.Decode(frame.Id);
                return identifier.SourceAddress == latestIdentifier.SourceAddress;
            });
        }
        else
        {
            senderFrames = usable.Where(frame =>
                frame.Id == latest.Id && frame.IsExtended == latest.IsExtended);
        }

        var windowStart = latest.Timestamp - options.StatisticsWindow;
        var windowFrames = senderFrames
            .Where(frame => frame.Timestamp >= windowStart &&
                            frame.Timestamp <= latest.Timestamp)
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

        if (windowFrames.Length == 0)
        {
            return NoData(
                signal,
                unit,
                "После фильтрации текущего sender статистическое окно не содержит кадров.",
                shortDlcCount);
        }

        var samples = windowFrames.Select(frame =>
        {
            var decoded = MachineSignalDecoder.Decode(signal, frame.Data);
            var raw = signal.IsSigned
                ? (double)decoded.RawSigned!.Value
                : decoded.RawUnsigned;
            return new MonitorSample(
                frame.Timestamp,
                frame.Id,
                raw,
                decoded.EngineeringValue);
        }).ToArray();

        var current = samples[^1];
        var age = referenceTime > current.Timestamp
            ? referenceTime - current.Timestamp
            : TimeSpan.Zero;
        var stale = age > options.StaleAfter;
        var rate = CalculateRate(samples, options.MinimumRateSpan);

        return new LivePhysicalSignalReading
        {
            SignalId = signal.SignalId,
            SignalName = signal.Name,
            Unit = unit,
            State = stale ? LivePhysicalSignalState.Stale : LivePhysicalSignalState.Fresh,
            CurrentRaw = current.Raw,
            CurrentPhysical = current.Physical,
            MinimumPhysical = samples.Min(sample => sample.Physical),
            MaximumPhysical = samples.Max(sample => sample.Physical),
            RatePerSecond = rate,
            SampleCount = samples.Length,
            LatestTimestamp = current.Timestamp,
            Age = age,
            LatestObservedCanId = current.CanId,
            ObservedCanIds = samples.Select(sample => sample.CanId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray(),
            J1939Pgn = pgn,
            J1939SourceAddress = sourceAddress,
            J1939DestinationAddress = destinationAddress,
            MatchingFramesWithShortDlc = shortDlcCount,
            StatusMessage = stale
                ? $"STALE: последний кадр {age.TotalMilliseconds:0} ms назад; предел {options.StaleAfter.TotalMilliseconds:0} ms."
                : $"FRESH: последний кадр {age.TotalMilliseconds:0} ms назад."
        };
    }

    private static LivePhysicalSignalReading NoData(
        MachineSignal signal,
        string unit,
        string message,
        int shortDlcCount) => new()
    {
        SignalId = signal.SignalId,
        SignalName = signal.Name,
        Unit = unit,
        State = LivePhysicalSignalState.NoData,
        MatchingFramesWithShortDlc = shortDlcCount,
        StatusMessage = message
    };

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

    private static bool IsBaseReceiveDataFrame(CanFrame frame) =>
        frame.Protocol == BusProtocol.ClassicalCan &&
        frame.Direction == CanDirection.Rx &&
        !frame.IsRemote &&
        !frame.IsError;

    private static double? CalculateRate(
        IReadOnlyList<MonitorSample> samples,
        TimeSpan minimumRateSpan)
    {
        if (samples.Count < 2)
            return null;

        var duration = samples[^1].Timestamp - samples[0].Timestamp;
        if (duration < minimumRateSpan)
            return null;

        var origin = samples[0].Timestamp;
        var x = samples.Select(sample => (sample.Timestamp - origin).TotalSeconds).ToArray();
        var y = samples.Select(sample => sample.Physical).ToArray();
        var xMean = x.Average();
        var yMean = y.Average();
        var denominator = x.Sum(value => (value - xMean) * (value - xMean));
        if (denominator <= double.Epsilon)
            return null;

        var numerator = 0.0;
        for (var index = 0; index < x.Length; index++)
            numerator += (x[index] - xMean) * (y[index] - yMean);

        var slope = numerator / denominator;
        return double.IsFinite(slope) ? slope : null;
    }

    private static void ValidateOptions(LivePhysicalSignalMonitorOptions options)
    {
        if (options.StatisticsWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.StatisticsWindow));
        if (options.StaleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.StaleAfter));
        if (options.MinimumRateSpan < TimeSpan.Zero ||
            options.MinimumRateSpan > options.StatisticsWindow)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumRateSpan));
    }

    private sealed record MonitorSample(
        DateTimeOffset Timestamp,
        uint CanId,
        double Raw,
        double Physical);
}
