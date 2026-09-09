using CraneCAN.Core.Models;

namespace CraneCAN.Core.Live;

public enum NodeHealthEventKind
{
    Timeout,
    Recovered
}

public sealed record NodeHealthEvent(
    uint Id,
    bool IsExtended,
    NodeHealthEventKind Kind,
    DateTimeOffset DetectedAt,
    DateTimeOffset LastSeen,
    TimeSpan ExpectedPeriod,
    TimeSpan Timeout,
    int SampleCount,
    string Description);

public sealed record NodeHealthSummary(
    int TrackedNodes,
    int ArmedNodes,
    int TimedOutNodes);

/// <summary>
/// Passive health monitor for periodic CAN identifiers.
/// "Node" here means one observed (ID, Standard/Extended) stream; it does not
/// claim that a CAN ID uniquely identifies a physical ECU.
/// </summary>
public sealed class NodeHealthMonitor
{
    private readonly object _sync = new();
    private readonly Dictionary<(uint Id, bool IsExtended), NodeState> _nodes = [];
    private readonly int _minimumIntervals;
    private readonly TimeSpan _minimumLearningDuration;
    private readonly TimeSpan _minimumTimeout;
    private readonly TimeSpan _maximumTimeout;
    private readonly TimeSpan _maximumPeriodToArm;
    private readonly double _timeoutMultiplier;
    private readonly double _maximumPeriodSpreadRatio;
    private readonly int _maximumTrackedNodes;

    public NodeHealthMonitor(
        int minimumIntervals = 8,
        TimeSpan? minimumLearningDuration = null,
        TimeSpan? minimumTimeout = null,
        TimeSpan? maximumTimeout = null,
        TimeSpan? maximumPeriodToArm = null,
        double timeoutMultiplier = 5.0,
        double maximumPeriodSpreadRatio = 2.5,
        int maximumTrackedNodes = 2048)
    {
        _minimumIntervals = minimumIntervals;
        _minimumLearningDuration = minimumLearningDuration ?? TimeSpan.FromSeconds(10);
        _minimumTimeout = minimumTimeout ?? TimeSpan.FromMilliseconds(250);
        _maximumTimeout = maximumTimeout ?? TimeSpan.FromSeconds(5);
        _maximumPeriodToArm = maximumPeriodToArm ?? TimeSpan.FromSeconds(1);
        _timeoutMultiplier = timeoutMultiplier;
        _maximumPeriodSpreadRatio = maximumPeriodSpreadRatio;
        _maximumTrackedNodes = maximumTrackedNodes;

        if (_minimumIntervals < 2 ||
            _minimumLearningDuration <= TimeSpan.Zero ||
            _minimumTimeout <= TimeSpan.Zero ||
            _maximumTimeout < _minimumTimeout ||
            _maximumPeriodToArm <= TimeSpan.Zero ||
            !double.IsFinite(_timeoutMultiplier) || _timeoutMultiplier <= 1 ||
            !double.IsFinite(_maximumPeriodSpreadRatio) || _maximumPeriodSpreadRatio < 1 ||
            _maximumTrackedNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumIntervals));
    }

    public NodeHealthSummary Summary
    {
        get
        {
            lock (_sync)
                return new NodeHealthSummary(
                    _nodes.Count,
                    _nodes.Values.Count(node => node.Armed),
                    _nodes.Values.Count(node => node.TimedOut));
        }
    }

    public NodeHealthEvent? Observe(CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.Validate();
        if (frame.Protocol != BusProtocol.ClassicalCan ||
            frame.Direction != CanDirection.Rx ||
            frame.IsRemote ||
            frame.IsError)
            return null;

        lock (_sync)
        {
            var key = (frame.Id, frame.IsExtended);
            if (!_nodes.TryGetValue(key, out var state))
            {
                if (_nodes.Count >= _maximumTrackedNodes)
                    return null;
                _nodes[key] = new NodeState(frame.Timestamp);
                return null;
            }

            if (frame.Timestamp < state.LastSeen)
                return null;

            var wasTimedOut = state.TimedOut;
            var previousLastSeen = state.LastSeen;
            var periodBeforeRecovery = state.ExpectedPeriod;
            var timeoutBeforeRecovery = state.Timeout;

            if (!wasTimedOut)
            {
                var interval = frame.Timestamp - state.LastSeen;
                if (interval > TimeSpan.Zero)
                {
                    state.Periods.Enqueue(interval.TotalMilliseconds);
                    while (state.Periods.Count > 32) state.Periods.Dequeue();
                }
            }

            state.LastSeen = frame.Timestamp;
            state.SampleCount++;
            state.TimedOut = false;
            Recalculate(state);

            if (!wasTimedOut)
                return null;

            return new NodeHealthEvent(
                frame.Id,
                frame.IsExtended,
                NodeHealthEventKind.Recovered,
                frame.Timestamp,
                previousLastSeen,
                periodBeforeRecovery,
                timeoutBeforeRecovery,
                state.SampleCount,
                "Периодический CAN ID снова появился после ранее зафиксированного таймаута.");
        }
    }

    public IReadOnlyList<NodeHealthEvent> Evaluate(DateTimeOffset now)
    {
        lock (_sync)
        {
            var result = new List<NodeHealthEvent>();
            foreach (var pair in _nodes)
            {
                var state = pair.Value;
                if (!state.Armed || state.TimedOut || now < state.LastSeen)
                    continue;
                if (now - state.LastSeen < state.Timeout)
                    continue;

                state.TimedOut = true;
                result.Add(new NodeHealthEvent(
                    pair.Key.Id,
                    pair.Key.IsExtended,
                    NodeHealthEventKind.Timeout,
                    now,
                    state.LastSeen,
                    state.ExpectedPeriod,
                    state.Timeout,
                    state.SampleCount,
                    "Стабильно периодический CAN ID не появился в рассчитанный срок."));
            }

            return result
                .OrderBy(item => item.DetectedAt)
                .ThenBy(item => item.Id)
                .ThenBy(item => item.IsExtended)
                .ToArray();
        }
    }

    private void Recalculate(NodeState state)
    {
        state.Armed = false;
        if (state.Periods.Count < _minimumIntervals ||
            state.LastSeen - state.FirstSeen < _minimumLearningDuration)
            return;

        var sorted = state.Periods.OrderBy(value => value).ToArray();
        var median = Percentile(sorted, 0.5);
        var p10 = Percentile(sorted, 0.1);
        var p90 = Percentile(sorted, 0.9);
        if (median <= 0 ||
            median > _maximumPeriodToArm.TotalMilliseconds ||
            p10 <= 0 ||
            p90 / p10 > _maximumPeriodSpreadRatio)
            return;

        var timeoutMilliseconds = Math.Clamp(
            median * _timeoutMultiplier,
            _minimumTimeout.TotalMilliseconds,
            _maximumTimeout.TotalMilliseconds);

        state.ExpectedPeriod = TimeSpan.FromMilliseconds(median);
        state.Timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        state.Armed = true;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 1) return sorted[0];
        var position = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private sealed class NodeState(DateTimeOffset firstSeen)
    {
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public DateTimeOffset LastSeen { get; set; } = firstSeen;
        public int SampleCount { get; set; } = 1;
        public Queue<double> Periods { get; } = new();
        public bool Armed { get; set; }
        public bool TimedOut { get; set; }
        public TimeSpan ExpectedPeriod { get; set; }
        public TimeSpan Timeout { get; set; }
    }
}
