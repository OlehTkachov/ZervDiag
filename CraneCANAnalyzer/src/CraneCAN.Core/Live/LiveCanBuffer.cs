using CraneCAN.Core.Models;

namespace CraneCAN.Core.Live;

public sealed class LiveCanBuffer
{
    public const int DefaultMaximumFrameCount = 1_000_000;
    private readonly object _sync = new();
    private readonly Queue<CanFrame> _frames = new();
    private readonly TimeSpan _retention;
    private readonly int _maximumFrameCount;
    private long _totalReceived;
    private long _evicted;

    public LiveCanBuffer(TimeSpan? retention = null, int maximumFrameCount = DefaultMaximumFrameCount)
    {
        _retention = retention ?? TimeSpan.FromSeconds(120);
        if (_retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        if (maximumFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameCount));
        }

        _maximumFrameCount = maximumFrameCount;
    }

    public long TotalReceived { get { lock (_sync) return _totalReceived; } }
    public long EvictedFrames { get { lock (_sync) return _evicted; } }
    public int Count { get { lock (_sync) return _frames.Count; } }

    public void Append(CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.Validate();
        lock (_sync)
        {
            _frames.Enqueue(frame);
            _totalReceived++;
            var cutoff = frame.Timestamp - _retention;
            while (_frames.Count > 0 &&
                   (_frames.Peek().Timestamp < cutoff || _frames.Count > _maximumFrameCount))
            {
                _frames.Dequeue();
                _evicted++;
            }
        }
    }

    public void AppendRange(IEnumerable<CanFrame> frames)
    {
        foreach (var frame in frames)
        {
            Append(frame);
        }
    }

    public IReadOnlyList<CanFrame> Snapshot()
    {
        lock (_sync) return _frames.ToArray();
    }

    public IReadOnlyList<CanFrame> GetRange(DateTimeOffset startInclusive, DateTimeOffset endExclusive)
    {
        if (endExclusive <= startInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(endExclusive));
        }

        lock (_sync)
        {
            return _frames.Where(frame => frame.Timestamp >= startInclusive &&
                                          frame.Timestamp < endExclusive).ToArray();
        }
    }
}
