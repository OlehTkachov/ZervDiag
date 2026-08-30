using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Live;

public sealed class LiveExperimentSession
{
    private readonly object _sync = new();
    private readonly List<CanFrame> _frames = [];
    private readonly List<LiveStateTransition> _transitions = [];
    private readonly List<LiveSessionWarning> _warnings = [];
    private LiveExperimentBoundaries? _boundaries;

    public LiveExperimentSession(LiveExperimentConfiguration configuration)
    {
        configuration.Validate();
        Configuration = configuration;
    }

    public Guid SessionId { get; } = Guid.NewGuid();
    public LiveExperimentConfiguration Configuration { get; }
    public LiveExperimentState State { get; private set; } = LiveExperimentState.Idle;
    public LiveExperimentOutcome Outcome { get; private set; } = LiveExperimentOutcome.Pending;
    public LiveExperimentBoundaries? Boundaries { get { lock (_sync) return _boundaries; } }
    public IReadOnlyList<LiveSessionWarning> Warnings { get { lock (_sync) return _warnings.ToArray(); } }
    public int CapturedFrameCount { get { lock (_sync) return _frames.Count; } }

    public void Start(DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            RequireState(LiveExperimentState.Idle);
            var baselineEnd = timestamp + Configuration.BaselineDuration;
            var actionStart = baselineEnd + Configuration.ActionLeadInDuration;
            var actionEnd = actionStart + Configuration.ActionDuration;
            _boundaries = new LiveExperimentBoundaries(
                timestamp, baselineEnd, actionStart, actionEnd, actionEnd + Configuration.PostActionDuration);
            TransitionTo(LiveExperimentState.Baseline, timestamp);
        }
    }

    public LiveExperimentState AdvanceTo(DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            if (_boundaries is null) throw new InvalidOperationException("Live-эксперимент не запущен.");
            if (State is LiveExperimentState.Completed or LiveExperimentState.Aborted) return State;
            if (timestamp < _boundaries.BaselineStart) throw new ArgumentOutOfRangeException(nameof(timestamp));
            if (timestamp >= _boundaries.BaselineEnd && State == LiveExperimentState.Baseline)
                TransitionTo(LiveExperimentState.WaitingForAction, _boundaries.BaselineEnd);
            if (timestamp >= _boundaries.ActionStart && State == LiveExperimentState.WaitingForAction)
                TransitionTo(LiveExperimentState.Action, _boundaries.ActionStart);
            if (timestamp >= _boundaries.ActionEnd && State == LiveExperimentState.Action)
                TransitionTo(LiveExperimentState.PostAction, _boundaries.ActionEnd);
            if (timestamp >= _boundaries.PostActionEnd && State == LiveExperimentState.PostAction)
                TransitionTo(LiveExperimentState.Analyzing, _boundaries.PostActionEnd);
            return State;
        }
    }

    public void AppendFrame(CanFrame frame)
    {
        frame.Validate();
        if (frame.Protocol != BusProtocol.ClassicalCan || frame.Direction != CanDirection.Rx || frame.IsRemote || frame.IsError) return;
        lock (_sync)
        {
            if (_boundaries is null || State is LiveExperimentState.Idle or LiveExperimentState.Completed or LiveExperimentState.Aborted) return;
            AdvanceTo(frame.Timestamp);
            if (frame.Timestamp >= _boundaries.BaselineStart && frame.Timestamp < _boundaries.PostActionEnd)
                _frames.Add(frame);
        }
    }

    public void AddWarning(LiveSessionWarningCode code, string message, DateTimeOffset timestamp, bool invalidatesExperiment)
    {
        lock (_sync) _warnings.Add(new LiveSessionWarning(code, message, timestamp, invalidatesExperiment));
    }

    public void Invalidate(LiveSessionWarningCode code, string message, DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            if (State is LiveExperimentState.Completed or LiveExperimentState.Aborted) return;
            _warnings.Add(new LiveSessionWarning(code, message, timestamp, true));
            Outcome = LiveExperimentOutcome.Invalid;
            TransitionTo(LiveExperimentState.Aborted, timestamp);
        }
    }

    public void Abort(DateTimeOffset timestamp, string reason = "Эксперимент остановлен оператором.")
    {
        lock (_sync)
        {
            if (State is LiveExperimentState.Completed or LiveExperimentState.Aborted) return;
            _warnings.Add(new LiveSessionWarning(LiveSessionWarningCode.OperatorAborted, reason, timestamp, true));
            Outcome = LiveExperimentOutcome.Aborted;
            TransitionTo(LiveExperimentState.Aborted, timestamp);
        }
    }

    public LiveExperimentResult Analyze()
    {
        lock (_sync)
        {
            RequireState(LiveExperimentState.Analyzing);
            var b = _boundaries!;
            var reference = Select(b.BaselineStart, b.BaselineEnd);
            var action = Select(b.ActionStart, b.ActionEnd);
            var post = Select(b.ActionEnd, b.PostActionEnd);
            if (_frames.Count == 0) _warnings.Add(new LiveSessionWarning(
                LiveSessionWarningCode.NoFrames, "Во время опыта не получено CAN-кадров.", b.PostActionEnd, true));
            var source = $"live://{SessionId:N}";
            var run = new GuidedExperimentRun(
                Configuration.RepeatNumber, Configuration.Bus, reference, action, b.ActionStart,
                Configuration.EventSearchTolerance, post,
                new CraneCAN.Core.Analysis.TraceWindow(0, (b.BaselineEnd - b.BaselineStart).TotalMilliseconds),
                new CraneCAN.Core.Analysis.TraceWindow((b.ActionStart - b.BaselineStart).TotalMilliseconds,
                    (b.ActionEnd - b.BaselineStart).TotalMilliseconds), source, source, Configuration.Bus, Configuration.Bus);
            var analysis = GuidedDiagnosticsAnalyzer.Analyze(Configuration.ActionName, [run]);
            var invalid = _warnings.Any(w => w.InvalidatesExperiment) || !analysis.Quality.CanAnalyze;
            if (invalid) analysis = analysis with { Candidates = [] };
            Outcome = invalid ? LiveExperimentOutcome.Invalid : LiveExperimentOutcome.Valid;
            TransitionTo(LiveExperimentState.Completed, b.PostActionEnd);
            return new LiveExperimentResult(SessionId, Outcome, Configuration, b, run, analysis,
                _frames.ToArray(), _transitions.ToArray(), _warnings.ToArray());
        }
    }

    private CanFrame[] Select(DateTimeOffset start, DateTimeOffset end) =>
        _frames.Where(frame => frame.Timestamp >= start && frame.Timestamp < end).ToArray();
    private void RequireState(LiveExperimentState expected)
    {
        if (State != expected) throw new InvalidOperationException($"Ожидалось состояние {expected}, текущее {State}.");
    }
    private void TransitionTo(LiveExperimentState state, DateTimeOffset timestamp)
    {
        State = state;
        _transitions.Add(new LiveStateTransition(state, timestamp));
    }
}
