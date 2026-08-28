using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Analysis;

public sealed record TraceWindow(double StartMilliseconds, double EndMilliseconds)
{
    public void Validate()
    {
        if (StartMilliseconds < 0 || EndMilliseconds <= StartMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(EndMilliseconds),
                "Конец временного окна должен быть больше начала, а начало не может быть отрицательным.");
        }
    }
}

public sealed record GenericTraceExperimentResult(
    int ReferenceFrameCount,
    int ActionFrameCount,
    IReadOnlyList<GenericCanComparison> Comparisons)
{
    public IReadOnlyList<GenericCanComparison> ChangedOnly =>
        Comparisons.Where(row => row.IsChanged).ToArray();
}

public static class GenericTraceExperiment
{
    public static async Task<GenericTraceExperimentResult> CompareFilesAsync(
        string referenceTrcPath,
        string actionTrcPath,
        int channel = 0,
        CancellationToken cancellationToken = default)
    {
        var reference = await PcanTrcCodec.LoadAsync(referenceTrcPath, channel, cancellationToken)
            .ConfigureAwait(false);
        var action = await PcanTrcCodec.LoadAsync(actionTrcPath, channel, cancellationToken)
            .ConfigureAwait(false);
        return Compare(reference, action);
    }

    public static async Task<GenericTraceExperimentResult> CompareWindowsAsync(
        string trcPath,
        TraceWindow referenceWindow,
        TraceWindow actionWindow,
        int channel = 0,
        CancellationToken cancellationToken = default)
    {
        referenceWindow.Validate();
        actionWindow.Validate();
        var frames = await PcanTrcCodec.LoadAsync(trcPath, channel, cancellationToken)
            .ConfigureAwait(false);
        return CompareWindows(frames, referenceWindow, actionWindow);
    }

    public static GenericTraceExperimentResult CompareWindows(
        IReadOnlyList<CanFrame> frames,
        TraceWindow referenceWindow,
        TraceWindow actionWindow)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("Трасса не содержит кадров для выбора временных окон.");
        }

        referenceWindow.Validate();
        actionWindow.Validate();
        var origin = frames.Min(frame => frame.Timestamp);
        var reference = SelectWindow(frames, origin, referenceWindow);
        var action = SelectWindow(frames, origin, actionWindow);
        return Compare(reference, action);
    }

    public static GenericTraceExperimentResult Compare(
        IEnumerable<CanFrame> referenceFrames,
        IEnumerable<CanFrame> actionFrames)
    {
        var reference = referenceFrames.Where(IsComparableFrame).ToArray();
        var action = actionFrames.Where(IsComparableFrame).ToArray();

        if (reference.Length == 0)
        {
            throw new InvalidOperationException(
                "В выбранном окне REFERENCE нет обычных Rx-кадров Classical CAN. " +
                "Проверьте границы времени и содержимое TRC.");
        }
        if (action.Length == 0)
        {
            throw new InvalidOperationException(
                "В выбранном окне ACTION нет обычных Rx-кадров Classical CAN. " +
                "Проверьте границы времени и содержимое TRC.");
        }

        var comparisons = GenericExperimentComparator.Compare(reference, action);
        return new GenericTraceExperimentResult(reference.Length, action.Length, comparisons);
    }

    private static CanFrame[] SelectWindow(
        IReadOnlyList<CanFrame> frames,
        DateTimeOffset origin,
        TraceWindow window)
    {
        var start = origin.AddMilliseconds(window.StartMilliseconds);
        var end = origin.AddMilliseconds(window.EndMilliseconds);
        return frames.Where(frame => frame.Timestamp >= start && frame.Timestamp < end).ToArray();
    }

    private static bool IsComparableFrame(CanFrame frame) =>
        frame.Protocol == BusProtocol.ClassicalCan &&
        frame.Direction == CanDirection.Rx &&
        !frame.IsRemote &&
        !frame.IsError;
}
