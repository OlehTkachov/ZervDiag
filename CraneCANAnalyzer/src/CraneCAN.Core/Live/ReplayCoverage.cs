using CraneCAN.Core.Models;

namespace CraneCAN.Core.Live;

public sealed record ReplayCoverage(DateTimeOffset? Start, TimeSpan Available, TimeSpan Required,
    bool Ordered, bool HasReceiveFrames)
{
    public bool CanComplete => Start.HasValue && Ordered && HasReceiveFrames && Available >= Required;

    public static ReplayCoverage Inspect(IEnumerable<CanFrame> frames, LiveExperimentConfiguration configuration)
    {
        configuration.Validate();
        DateTimeOffset? first = null, last = null;
        var ordered = true;
        var hasReceive = false;
        foreach (var frame in frames)
        {
            first ??= frame.Timestamp;
            if (last.HasValue && frame.Timestamp < last.Value) ordered = false;
            last = frame.Timestamp;
            hasReceive |= frame.Protocol == BusProtocol.ClassicalCan && frame.Direction == CanDirection.Rx &&
                !frame.IsError && !frame.IsRemote;
        }
        return new(first, first.HasValue ? last!.Value - first.Value : TimeSpan.Zero,
            configuration.BaselineDuration + configuration.ActionLeadInDuration + configuration.ActionDuration +
            configuration.PostActionDuration, ordered, hasReceive);
    }
}
