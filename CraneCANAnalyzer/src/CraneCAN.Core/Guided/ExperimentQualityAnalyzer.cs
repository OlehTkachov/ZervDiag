using CraneCAN.Core.Models;

namespace CraneCAN.Core.Guided;

public static class ExperimentQualityAnalyzer
{
    public const int RecommendedMinimumFrameCount = 3;
    public const double RecommendedMinimumWindowMilliseconds = 40;

    public static ExperimentQualityReport Evaluate(IReadOnlyList<GuidedExperimentRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var issues = new List<ExperimentQualityIssue>();
        if (runs.Count == 0)
        {
            issues.Add(new ExperimentQualityIssue(
                ExperimentQualityCode.EmptyReference, ExperimentQualitySeverity.Error));
            issues.Add(new ExperimentQualityIssue(
                ExperimentQualityCode.EmptyAction, ExperimentQualitySeverity.Error));
            return new ExperimentQualityReport(issues);
        }

        var expectedBus = runs[0].Bus;
        foreach (var run in runs)
        {
            var reference = Comparable(run.ReferenceFrames);
            var action = Comparable(run.ActionFrames);
            if (reference.Count == 0)
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.EmptyReference, ExperimentQualitySeverity.Error, run.RepeatNumber));
            }
            else if (reference.Count < RecommendedMinimumFrameCount)
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.TooFewReferenceFrames, ExperimentQualitySeverity.Warning, run.RepeatNumber));
            }

            if (action.Count == 0)
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.EmptyAction, ExperimentQualitySeverity.Error, run.RepeatNumber));
            }
            else if (action.Count < RecommendedMinimumFrameCount)
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.TooFewActionFrames, ExperimentQualitySeverity.Warning, run.RepeatNumber));
            }

            CheckDuration(reference, ExperimentQualityCode.TooShortReference, run.RepeatNumber, issues);
            CheckDuration(action, ExperimentQualityCode.TooShortAction, run.RepeatNumber, issues);

            if (!string.Equals(run.Bus, expectedBus, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.DifferentBuses, ExperimentQualitySeverity.Error, run.RepeatNumber));
            }

            if (!string.IsNullOrWhiteSpace(run.ReferenceBus) &&
                !string.IsNullOrWhiteSpace(run.ActionBus) &&
                !string.Equals(run.ReferenceBus, run.ActionBus, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.DifferentBuses, ExperimentQualitySeverity.Error, run.RepeatNumber));
            }

            if (run.ReferencePath is not null && run.ActionPath is not null &&
                string.Equals(run.ReferencePath, run.ActionPath, StringComparison.OrdinalIgnoreCase) &&
                run.ReferenceWindow is not null && run.ActionWindow is not null &&
                WindowsOverlap(run.ReferenceWindow, run.ActionWindow))
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.OverlappingWindows, ExperimentQualitySeverity.Error, run.RepeatNumber));
            }

            else if (run.ReferencePath is not null && run.ActionPath is not null &&
                     string.Equals(run.ReferencePath, run.ActionPath, StringComparison.OrdinalIgnoreCase) &&
                     run.ReferenceWindow is not null && run.ActionWindow is not null &&
                     run.ReferenceWindow.StartMilliseconds > run.ActionWindow.StartMilliseconds)
            {
                issues.Add(new ExperimentQualityIssue(
                    ExperimentQualityCode.ReferenceAfterAction, ExperimentQualitySeverity.Warning, run.RepeatNumber));
            }
        }

        if (issues.Count == 0)
        {
            issues.Add(new ExperimentQualityIssue(
                ExperimentQualityCode.Ready, ExperimentQualitySeverity.Information));
        }

        return new ExperimentQualityReport(issues);
    }

    private static IReadOnlyList<CanFrame> Comparable(IReadOnlyList<CanFrame> frames) => frames
        .Where(frame => frame.Protocol == BusProtocol.ClassicalCan &&
                        frame.Direction == CanDirection.Rx &&
                        !frame.IsRemote &&
                        !frame.IsError)
        .ToArray();

    private static void CheckDuration(
        IReadOnlyList<CanFrame> frames,
        ExperimentQualityCode code,
        int repeatNumber,
        ICollection<ExperimentQualityIssue> issues)
    {
        if (frames.Count < 2)
        {
            return;
        }

        var duration = (frames[^1].Timestamp - frames[0].Timestamp).TotalMilliseconds;
        if (duration < RecommendedMinimumWindowMilliseconds)
        {
            issues.Add(new ExperimentQualityIssue(code, ExperimentQualitySeverity.Warning, repeatNumber));
        }
    }

    private static bool WindowsOverlap(Analysis.TraceWindow first, Analysis.TraceWindow second) =>
        first.StartMilliseconds < second.EndMilliseconds &&
        second.StartMilliseconds < first.EndMilliseconds;
}
