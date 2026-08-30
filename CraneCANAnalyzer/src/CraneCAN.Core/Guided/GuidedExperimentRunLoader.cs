using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Guided;

public static class GuidedExperimentRunLoader
{
    public static async Task<GuidedExperimentRun> LoadAsync(GuidedExperimentRepeat definition,
        CancellationToken cancellationToken = default)
    {
        if (definition.ReferenceSource.Window is null || definition.ActionSource.Window is null)
            throw new InvalidDataException($"Повтор {definition.RepeatNumber} не содержит временных окон.");
        var reference = await PcanTrcCodec.LoadAsync(definition.ReferenceSource.Path,
            definition.ReferenceSource.Channel, cancellationToken).ConfigureAwait(false);
        var action = string.Equals(definition.ReferenceSource.Path, definition.ActionSource.Path, StringComparison.OrdinalIgnoreCase)
            ? reference : await PcanTrcCodec.LoadAsync(definition.ActionSource.Path,
                definition.ActionSource.Channel, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CanFrame>? returned = null;
        if (definition.ReturnSource?.Window is { } returnWindow)
        {
            var trace = string.Equals(definition.ReturnSource.Path, definition.ReferenceSource.Path, StringComparison.OrdinalIgnoreCase)
                ? reference : string.Equals(definition.ReturnSource.Path, definition.ActionSource.Path, StringComparison.OrdinalIgnoreCase)
                    ? action : await PcanTrcCodec.LoadAsync(definition.ReturnSource.Path,
                        definition.ReturnSource.Channel, cancellationToken).ConfigureAwait(false);
            returned = Select(trace, returnWindow);
        }
        var actionOrigin = action.Min(frame => frame.Timestamp);
        return new GuidedExperimentRun(definition.RepeatNumber, definition.ActionSource.Bus,
            Select(reference, definition.ReferenceSource.Window), Select(action, definition.ActionSource.Window),
            definition.ActionApproximateTimeMilliseconds.HasValue
                ? actionOrigin.AddMilliseconds(definition.ActionApproximateTimeMilliseconds.Value) : null,
            TimeSpan.FromMilliseconds(definition.EventSearchToleranceMilliseconds), returned,
            definition.ReferenceSource.Window, definition.ActionSource.Window,
            definition.ReferenceSource.Path, definition.ActionSource.Path,
            definition.ReferenceSource.Bus, definition.ActionSource.Bus);
    }

    private static CanFrame[] Select(IReadOnlyList<CanFrame> frames, TraceWindow window)
    {
        var origin = frames.Min(frame => frame.Timestamp);
        var start = origin.AddMilliseconds(window.StartMilliseconds);
        var end = origin.AddMilliseconds(window.EndMilliseconds);
        return frames.Where(frame => frame.Timestamp >= start && frame.Timestamp < end).ToArray();
    }
}
