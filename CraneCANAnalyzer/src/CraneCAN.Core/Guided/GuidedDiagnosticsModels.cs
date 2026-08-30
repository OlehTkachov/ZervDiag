using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Guided;

public enum SignalKnowledgeState
{
    Unknown,
    Candidate,
    Probable,
    Confirmed,
    Rejected
}

public enum EvidenceKind
{
    RepeatedExperiment,
    ElectricalSchematic,
    HydraulicSchematic,
    ManufacturerDocumentation,
    Dbc,
    J1939Database,
    ControllerConfiguration,
    Mpf,
    ServiceSoftware,
    MultimeterMeasurement,
    CurrentMeasurement,
    PhysicalOutputCheck,
    UserConfirmation
}

public sealed record SignalEvidence
{
    public EvidenceKind Kind { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? SourceReference { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ExperimentTraceSource
{
    public string Path { get; init; } = string.Empty;
    public string Bus { get; init; } = "CAN1";
    public int Channel { get; init; }
    public TraceWindow? Window { get; init; }
}

public sealed record GuidedExperimentRepeat
{
    public int RepeatNumber { get; init; }
    public ExperimentTraceSource ReferenceSource { get; init; } = new();
    public ExperimentTraceSource ActionSource { get; init; } = new();
    public double? ActionApproximateTimeMilliseconds { get; init; }
    public double EventSearchToleranceMilliseconds { get; init; } = 2_000;
    public ExperimentTraceSource? ReturnSource { get; init; }
}

public sealed record GuidedExperiment
{
    public Guid ExperimentId { get; init; } = Guid.NewGuid();
    public Guid? MachineProfileId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Bus { get; init; } = "CAN1";
    public string ActionName { get; init; } = string.Empty;
    public Guid RepeatedExperimentGroup { get; init; } = Guid.NewGuid();
    public string OperatorNotes { get; init; } = string.Empty;
    public string AnalysisVersion { get; init; } = "0.6.0";
    public List<GuidedExperimentRepeat> Repeats { get; init; } = [];
    public List<GuidedCandidateSnapshot> Candidates { get; init; } = [];
    public List<Guid> ConfirmedSignals { get; init; } = [];
    public List<LiveCaptureMetadata> LiveCaptures { get; init; } = [];
}

public sealed record LiveCaptureMetadata
{
    public Guid SessionId { get; init; }
    public int RepeatNumber { get; init; }
    public string DriverId { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public int Bitrate { get; init; }
    public bool ListenOnlyConfirmed { get; init; }
    public string RawCapturePath { get; init; } = string.Empty;
    public DateTimeOffset CaptureStart { get; init; }
    public DateTimeOffset BaselineStart { get; init; }
    public DateTimeOffset BaselineEnd { get; init; }
    public DateTimeOffset ActionStart { get; init; }
    public DateTimeOffset ActionEnd { get; init; }
    public DateTimeOffset PostActionEnd { get; init; }
    public string OperatorInstruction { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public long ReceivedFrames { get; init; }
    public long StandardFrames { get; init; }
    public long ExtendedFrames { get; init; }
    public long LostFrames { get; init; }
    public long ErrorFrames { get; init; }
    public List<string> QualityWarnings { get; init; } = [];
}

public sealed record GuidedExperimentRun(
    int RepeatNumber,
    string Bus,
    IReadOnlyList<CanFrame> ReferenceFrames,
    IReadOnlyList<CanFrame> ActionFrames,
    DateTimeOffset? ApproximateEventTime = null,
    TimeSpan? EventSearchTolerance = null,
    IReadOnlyList<CanFrame>? ReturnFrames = null,
    TraceWindow? ReferenceWindow = null,
    TraceWindow? ActionWindow = null,
    string? ReferencePath = null,
    string? ActionPath = null,
    string? ReferenceBus = null,
    string? ActionBus = null);

public enum ExperimentQualitySeverity
{
    Information,
    Warning,
    Error
}

public enum ExperimentQualityCode
{
    Ready,
    EmptyReference,
    EmptyAction,
    TooShortReference,
    TooShortAction,
    TooFewReferenceFrames,
    TooFewActionFrames,
    OverlappingWindows,
    ReferenceAfterAction,
    DifferentBuses,
    EventNotDetected,
    EventBoundaryUnclear,
    LowRepeatability
}

public sealed record ExperimentQualityIssue(
    ExperimentQualityCode Code,
    ExperimentQualitySeverity Severity,
    int? RepeatNumber = null);

public sealed record ExperimentQualityReport(IReadOnlyList<ExperimentQualityIssue> Issues)
{
    public bool CanAnalyze => Issues.All(issue => issue.Severity != ExperimentQualitySeverity.Error);
    public bool IsGood => CanAnalyze && Issues.All(issue => issue.Severity != ExperimentQualitySeverity.Warning);
}

public enum CandidateChangeKind
{
    StableBit,
    StableByte,
    Ramp,
    AnalogNoise,
    MessageAppeared,
    MessageDisappeared,
    DlcChanged
}

public enum ScoreReason
{
    RepeatedAllThreeOrMore,
    RepeatedAllAvailable,
    StableBitTransition,
    StableByteTransition,
    MessagePresenceChange,
    AnalogRamp,
    OccursAfterAction,
    ReturnsToBaseline,
    AgreementAbove95,
    ObservedInReference,
    LowRepeatability,
    AnalogNoise,
    OccursBeforeAction
}

public sealed record ScoreContribution(ScoreReason Reason, int Points);

public sealed record TemporalTransition
{
    public byte? BaselineValue { get; init; }
    public byte? FirstChangedValue { get; init; }
    public DateTimeOffset? FirstChangeTime { get; init; }
    public double? ReactionMilliseconds { get; init; }
    public IReadOnlyList<byte> Sequence { get; init; } = [];
    public byte? MinimumValue { get; init; }
    public byte? MaximumValue { get; init; }
    public byte? LastStableValue { get; init; }
    public double? StabilizationMilliseconds { get; init; }
    public bool ReturnedToBaseline { get; init; }
    public double? RatePerSecond { get; init; }
    public double MonotonicityPercent { get; init; }
    public int TransitionCount { get; init; }
}

public sealed record GuidedCandidate
{
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public int? DataIndex { get; init; }
    public int? BitIndex { get; init; }
    public byte? ReferenceValue { get; init; }
    public byte? ActionValue { get; init; }
    public CandidateChangeKind ChangeKind { get; init; }
    public string ActionName { get; init; } = string.Empty;
    public SignalKnowledgeState Status { get; init; } = SignalKnowledgeState.Candidate;
    public int RepeatabilityCount { get; init; }
    public int RepeatCount { get; init; }
    public double AgreementPercent { get; init; }
    public double? ReactionMilliseconds { get; init; }
    public TemporalTransition? Temporal { get; init; }
    public int Score { get; init; }
    public IReadOnlyList<ScoreContribution> ScoreExplanation { get; init; } = [];
    public IReadOnlyList<int> ObservedInRepeats { get; init; } = [];

    public string StableKey =>
        $"{Id:X8}:{(IsExtended ? 'E' : 'S')}:{DataIndex?.ToString() ?? "M"}:{BitIndex?.ToString() ?? "B"}";
}

public sealed record GuidedCandidateSnapshot
{
    public string StableKey { get; init; } = string.Empty;
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public int? DataIndex { get; init; }
    public int? BitIndex { get; init; }
    public byte? ReferenceValue { get; init; }
    public byte? ActionValue { get; init; }
    public int Score { get; init; }
    public int RepeatabilityCount { get; init; }
    public int RepeatCount { get; init; }
    public SignalKnowledgeState Status { get; init; } = SignalKnowledgeState.Candidate;
}

public sealed record GuidedTimelineEvent
{
    public DateTimeOffset Timestamp { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string CandidateKey { get; init; } = string.Empty;
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public int? DataIndex { get; init; }
    public byte? Value { get; init; }
    public int RepeatNumber { get; init; }
}

public sealed record GuidedAnalysisResult(
    ExperimentQualityReport Quality,
    IReadOnlyList<GuidedCandidate> Candidates,
    IReadOnlyList<GuidedTimelineEvent> Timeline,
    IReadOnlyDictionary<int, DateTimeOffset?> DetectedEventTimes);
