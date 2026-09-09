using CraneCAN.Core.Models;

namespace CraneCAN.Core.Live;

public sealed record IncidentMarker(DateTimeOffset Timestamp, string Label);

public sealed record PreFaultIncident(
    Guid IncidentId,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    DateTimeOffset ObservedUntil,
    IReadOnlyList<IncidentMarker> Markers,
    IReadOnlyList<CanFrame> Frames,
    IReadOnlyList<string> QualityCodes)
{
    public bool Complete => QualityCodes.Count == 0;
}

/// <summary>Receive-only pre/post capture. Times use the same clock as incoming frames.</summary>
public sealed class PreFaultRecorder
{
    private readonly object _sync = new();
    private readonly Queue<CanFrame> _history = new();
    private readonly TimeSpan _pre;
    private readonly TimeSpan _post;
    private readonly int _maximumFrames;
    private readonly List<CanFrame> _incidentFrames = [];
    private readonly List<IncidentMarker> _markers = [];
    private readonly HashSet<string> _issues = [];
    private DateTimeOffset? _firstObserved;
    private DateTimeOffset? _now;
    private DateTimeOffset? _capacityEvictedUntil;
    private DateTimeOffset _start;
    private DateTimeOffset _end;
    private Guid _id;
    private bool _active;
    private bool _streamUncertain;
    private PreFaultIncident? _last;

    public PreFaultRecorder(TimeSpan? pre = null, TimeSpan? post = null, int maximumFrames = 100_000)
    {
        _pre = pre ?? TimeSpan.FromSeconds(10);
        _post = post ?? TimeSpan.FromSeconds(5);
        if (_pre <= TimeSpan.Zero || _post <= TimeSpan.Zero || maximumFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        _maximumFrames = maximumFrames;
    }

    public bool IsCollecting { get { lock (_sync) return _active; } }
    public DateTimeOffset? ActiveWindowEnd { get { lock (_sync) return _active ? _end : null; } }
    public PreFaultIncident? LastIncident { get { lock (_sync) return _last; } }

    public void Append(CanFrame frame)
    {
        frame.Validate();
        if (frame.Protocol != BusProtocol.ClassicalCan || frame.Direction != CanDirection.Rx || frame.IsError || frame.IsRemote)
            return;
        lock (_sync)
        {
            if (_now.HasValue && frame.Timestamp < _now.Value)
            {
                _streamUncertain = true;
                if (_active) _issues.Add("OUT_OF_ORDER");
                return;
            }
            _firstObserved ??= frame.Timestamp;
            _now = frame.Timestamp;
            // Own the bytes: callers may reuse their receive buffer.
            var owned = frame with { Data = frame.Data.ToArray() };
            if (_active && frame.Timestamp < _end)
            {
                if (_incidentFrames.Count < _maximumFrames) _incidentFrames.Add(owned);
                else _issues.Add("FRAME_LIMIT");
            }
            _history.Enqueue(owned);
            // Keep pre + post so an automatic health trigger detected up to one post-window
            // late can still reconstruct the full prehistory without inventing frames.
            var historyRetention = _pre + _post;
            while (_history.Count > 0 && _history.Peek().Timestamp < frame.Timestamp - historyRetention)
                _history.Dequeue();
            while (_history.Count > _maximumFrames)
                _capacityEvictedUntil = _history.Dequeue().Timestamp;
            if (_active && frame.Timestamp >= _end) Finish(frame.Timestamp);
        }
    }

    public void Mark(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        lock (_sync)
        {
            if (!_now.HasValue) throw new InvalidOperationException("Сначала дождитесь CAN-кадров.");
            MarkAtLocked(_now.Value, label);
        }
    }

    public void MarkAt(DateTimeOffset timestamp, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        lock (_sync)
        {
            if (!_now.HasValue) throw new InvalidOperationException("Сначала дождитесь CAN-кадров.");
            MarkAtLocked(timestamp, label);
        }
    }

    private void MarkAtLocked(DateTimeOffset timestamp, string label)
    {
        if (_active)
        {
            if (timestamp >= _end)
                throw new InvalidOperationException("Новая отметка находится за пределами активного incident-окна.");
            if (_markers.Count >= 32)
                throw new InvalidOperationException("В одном событии допускается до 32 отметок.");
            _markers.Add(new IncidentMarker(timestamp, label));
            return; // Same incident; keep the original bounded post window.
        }

        _id = Guid.NewGuid();
        _start = timestamp - _pre;
        _end = timestamp + _post;
        _markers.Clear();
        _markers.Add(new IncidentMarker(timestamp, label));
        _incidentFrames.Clear();
        _incidentFrames.AddRange(_history.Where(frame =>
            frame.Timestamp >= _start && frame.Timestamp < _end));
        _issues.Clear();
        if (_firstObserved > _start) _issues.Add("SHORT_PREHISTORY");
        if (_capacityEvictedUntil >= _start) _issues.Add("PREHISTORY_FRAME_LIMIT");
        if (_streamUncertain) _issues.Add("CAPTURE_UNCERTAIN");

        if (_now!.Value >= _end)
            Finish(_now.Value);
        else
            _active = true;
    }

    public void MarkCaptureUncertain()
    {
        lock (_sync)
        {
            _streamUncertain = true;
            if (_active) _issues.Add("CAPTURE_UNCERTAIN");
        }
    }

    public void Stop(bool replayEnded = false)
    {
        lock (_sync)
        {
            if (!_active) return;
            _issues.Add("POST_INTERRUPTED");
            if (replayEnded) _issues.Add("REPLAY_ENDED");
            Finish(_now!.Value);
        }
    }

    private void Finish(DateTimeOffset observedUntil)
    {
        _last = new PreFaultIncident(_id, _start, _end, observedUntil,
            _markers.ToArray(), _incidentFrames.ToArray(), _issues.OrderBy(code => code).ToArray());
        _incidentFrames.Clear();
        _active = false;
    }
}
