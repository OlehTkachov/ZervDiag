using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;
using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Guided;

public static class GuidedExperimentRunLoader
{
    public static async Task<GuidedExperimentRun> LoadAsync(
        GuidedExperimentRepeat definition,
        CancellationToken cancellationToken = default)
    {
        if (definition.ReferenceSource.Window is null || definition.ActionSource.Window is null)
            throw new InvalidDataException($"Повтор {definition.RepeatNumber} не содержит временных окон.");

        var referencePath = ResolveDependency(
            definition.RepeatNumber,
            "REFERENCE",
            definition.ReferenceSource.Path);
        var actionPath = ResolveDependency(
            definition.RepeatNumber,
            "ACTION",
            definition.ActionSource.Path);

        var reference = await PcanTrcCodec.LoadAsync(
                referencePath,
                definition.ReferenceSource.Channel,
                cancellationToken)
            .ConfigureAwait(false);
        var action = string.Equals(
                referencePath,
                actionPath,
                StringComparison.OrdinalIgnoreCase)
            ? reference
            : await PcanTrcCodec.LoadAsync(
                    actionPath,
                    definition.ActionSource.Channel,
                    cancellationToken)
                .ConfigureAwait(false);

        IReadOnlyList<CanFrame>? returned = null;
        if (definition.ReturnSource?.Window is { } returnWindow)
        {
            var returnPath = ResolveDependency(
                definition.RepeatNumber,
                "RETURN",
                definition.ReturnSource.Path);
            var trace = string.Equals(
                    returnPath,
                    referencePath,
                    StringComparison.OrdinalIgnoreCase)
                ? reference
                : string.Equals(
                    returnPath,
                    actionPath,
                    StringComparison.OrdinalIgnoreCase)
                    ? action
                    : await PcanTrcCodec.LoadAsync(
                            returnPath,
                            definition.ReturnSource.Channel,
                            cancellationToken)
                        .ConfigureAwait(false);
            returned = Select(trace, returnWindow);
        }

        var actionOrigin = action.Min(frame => frame.Timestamp);
        return new GuidedExperimentRun(
            definition.RepeatNumber,
            definition.ActionSource.Bus,
            Select(reference, definition.ReferenceSource.Window),
            Select(action, definition.ActionSource.Window),
            definition.ActionApproximateTimeMilliseconds.HasValue
                ? actionOrigin.AddMilliseconds(
                    definition.ActionApproximateTimeMilliseconds.Value)
                : null,
            TimeSpan.FromMilliseconds(definition.EventSearchToleranceMilliseconds),
            returned,
            definition.ReferenceSource.Window,
            definition.ActionSource.Window,
            referencePath,
            actionPath,
            definition.ReferenceSource.Bus,
            definition.ActionSource.Bus);
    }

    private static string ResolveDependency(
        int repeatNumber,
        string role,
        string requestedPath)
    {
        var resolution = ProjectTraceDependencyResolver.Resolve(requestedPath);
        if (resolution.CanLoad)
            return resolution.ResolvedPath!;

        var candidates = resolution.Candidates is { Count: > 0 }
            ? " Кандидаты: " + string.Join(", ", resolution.Candidates) + "."
            : string.Empty;
        throw new FileNotFoundException(
            $"Повтор {repeatNumber}: TRC {role} не найден. " +
            resolution.Message + candidates,
            resolution.ResolvedPath ?? requestedPath);
    }

    private static CanFrame[] Select(
        IReadOnlyList<CanFrame> frames,
        TraceWindow window)
    {
        var origin = frames.Min(frame => frame.Timestamp);
        var start = origin.AddMilliseconds(window.StartMilliseconds);
        var end = origin.AddMilliseconds(window.EndMilliseconds);
        return frames
            .Where(frame => frame.Timestamp >= start && frame.Timestamp < end)
            .ToArray();
    }
}
