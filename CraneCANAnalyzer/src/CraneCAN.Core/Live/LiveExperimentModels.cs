using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Live;

public enum LiveExperimentState { Idle, Baseline, WaitingForAction, Action, PostAction, Analyzing, Completed, Aborted }
public enum LiveExperimentOutcome { Pending, Valid, Invalid, Aborted }
public enum LiveSessionWarningCode { NoFrames, DriverDisconnected, CanStreamStopped, BusError, FramesLost, OperatorAborted, InvalidSequence }

public sealed record LiveExperimentConfiguration
{
    public string ActionName { get; init; } = "CUSTOM_ACTION";
    public string OperatorInstruction { get; init; } = "Выполните одно контролируемое действие.";
    public string Bus { get; init; } = "CAN1";
    public int RepeatNumber { get; init; } = 1;
    public TimeSpan BaselineDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ActionLeadInDuration { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ActionDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PostActionDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan EventSearchTolerance { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ActionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(OperatorInstruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(Bus);
        if (RepeatNumber <= 0 || BaselineDuration <= TimeSpan.Zero || ActionLeadInDuration < TimeSpan.Zero ||
            ActionDuration <= TimeSpan.Zero || PostActionDuration <= TimeSpan.Zero || EventSearchTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LiveExperimentConfiguration),
                "Номер повтора и длительности live-этапов заданы неверно.");
        }
    }
}

public sealed record LiveExperimentBoundaries(
    DateTimeOffset BaselineStart, DateTimeOffset BaselineEnd, DateTimeOffset ActionStart,
    DateTimeOffset ActionEnd, DateTimeOffset PostActionEnd);
public sealed record LiveStateTransition(LiveExperimentState State, DateTimeOffset Timestamp);
public sealed record LiveSessionWarning(LiveSessionWarningCode Code, string Message, DateTimeOffset Timestamp, bool InvalidatesExperiment);
public sealed record LiveExperimentResult(
    Guid SessionId, LiveExperimentOutcome Outcome, LiveExperimentConfiguration Configuration,
    LiveExperimentBoundaries Boundaries, GuidedExperimentRun Run, GuidedAnalysisResult Analysis,
    IReadOnlyList<CanFrame> CapturedFrames, IReadOnlyList<LiveStateTransition> Transitions,
    IReadOnlyList<LiveSessionWarning> Warnings);
