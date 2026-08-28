using CraneCAN.Core.Models;

namespace CraneCAN.Core.Analysis;

public sealed class FrameStatisticsAggregator
{
    private readonly Dictionary<uint, MutableStatistics> _statistics = [];

    public void Add(CanFrame frame)
    {
        frame.Validate();
        var key = frame.Id;
        if (!_statistics.TryGetValue(key, out var value))
        {
            _statistics[key] = new MutableStatistics(frame);
            return;
        }

        value.Add(frame);
    }

    public IReadOnlyList<FrameStatistics> Snapshot() => _statistics
        .OrderBy(pair => pair.Key)
        .Select(pair => pair.Value.ToImmutable())
        .ToArray();

    public void Clear() => _statistics.Clear();

    private sealed class MutableStatistics
    {
        private readonly uint _id;
        private double _periodTotal;
        private long _periodCount;
        private double? _minimumPeriod;
        private double? _maximumPeriod;

        public MutableStatistics(CanFrame first)
        {
            _id = first.Id;
            Count = 1;
            Dlc = first.Dlc;
            FirstSeen = first.Timestamp;
            LastSeen = first.Timestamp;
        }

        public long Count { get; private set; }
        public int Dlc { get; private set; }
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen { get; private set; }

        public void Add(CanFrame frame)
        {
            var period = (frame.Timestamp - LastSeen).TotalMilliseconds;
            if (period >= 0)
            {
                _periodTotal += period;
                _periodCount++;
                _minimumPeriod = !_minimumPeriod.HasValue
                    ? period
                    : Math.Min(_minimumPeriod.Value, period);
                _maximumPeriod = !_maximumPeriod.HasValue
                    ? period
                    : Math.Max(_maximumPeriod.Value, period);
            }

            Count++;
            Dlc = frame.Dlc;
            LastSeen = frame.Timestamp;
        }

        public FrameStatistics ToImmutable() => new(
            _id.ToString("X2"),
            Count,
            Dlc,
            _periodCount == 0 ? null : _periodTotal / _periodCount,
            _minimumPeriod,
            _maximumPeriod,
            FirstSeen,
            LastSeen);
    }
}
