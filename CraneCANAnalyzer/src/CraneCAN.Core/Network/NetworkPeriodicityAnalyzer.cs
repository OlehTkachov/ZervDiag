using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class NetworkPeriodicityAnalyzer
{
    public static IReadOnlyList<NetworkPeriodicStreamSnapshot> Analyze(IReadOnlyList<CanFrame> frames)
    {
        var result = new List<NetworkPeriodicStreamSnapshot>();
        foreach (var group in frames.GroupBy(frame => (frame.Id, frame.IsExtended)))
        {
            var ordered = group.OrderBy(frame => frame.Timestamp).ToArray();
            if (ordered.Length == 0) continue;

            var intervals = new List<double>();
            for (var i = 1; i < ordered.Length; i++)
            {
                var ms = (ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalMilliseconds;
                if (ms > 0) intervals.Add(ms);
            }

            double? avg = null, median = null, min = null, max = null, jitter = null, timeout = null;
            var periodic = false;
            var confident = false;
            var heartbeat = !group.Key.IsExtended && group.Key.Id is >= 0x701 and <= 0x77F;

            if (intervals.Count >= 3)
            {
                var sorted = intervals.OrderBy(value => value).ToArray();
                median = GetMedian(sorted);
                min = sorted[0];
                max = sorted[^1];

                if (median > 0)
                {
                    var core = intervals
                        .Where(value => Math.Abs(value - median.Value) / median.Value <= 0.35)
                        .OrderBy(value => value)
                        .ToArray();
                    var agreement = (double)core.Length / intervals.Count;
                    if (core.Length > 0)
                    {
                        avg = core.Average();
                        jitter = (core[^1] - core[0]) / median.Value * 100.0;
                    }
                    else
                    {
                        avg = intervals.Average();
                        jitter = double.PositiveInfinity;
                    }

                    periodic = core.Length >= 3 && agreement >= 0.70 && jitter <= 50.0;
                    var observation = ordered[^1].Timestamp - ordered[0].Timestamp;
                    confident = periodic &&
                        ((observation >= TimeSpan.FromSeconds(10) && ordered.Length >= 10) ||
                         (heartbeat && ordered.Length >= 4 && agreement >= 0.75));
                    timeout = Math.Max(heartbeat ? 250.0 : 500.0, median.Value * 3.0);
                }
            }

            byte? sa = null;
            if (group.Key.IsExtended && J1939PassiveParser.TryParse(group.Key.Id, true, out var j1939))
                sa = j1939.SourceAddress;

            result.Add(new NetworkPeriodicStreamSnapshot
            {
                Id = group.Key.Id,
                IsExtended = group.Key.IsExtended,
                FrameCount = ordered.Length,
                FirstSeen = ordered[0].Timestamp,
                LastSeen = ordered[^1].Timestamp,
                AveragePeriodMilliseconds = avg,
                MedianPeriodMilliseconds = median,
                MinimumPeriodMilliseconds = min,
                MaximumPeriodMilliseconds = max,
                JitterPercent = jitter,
                TimeoutMilliseconds = timeout,
                ExpectedNextFrame = median.HasValue ? ordered[^1].Timestamp.AddMilliseconds(median.Value) : null,
                IsPeriodic = periodic,
                IsConfidentPeriodic = confident,
                IsCanopenHeartbeat = heartbeat,
                SourceAddress = sa,
                CanopenNodeId = heartbeat ? (byte)(group.Key.Id - 0x700) : null
            });
        }
        return result;
    }

    public static NetworkNodeHealthState DetermineNodeHealth(
        IReadOnlyList<NetworkPeriodicStreamSnapshot> streams,
        DateTimeOffset captureEnd)
    {
        if (streams.Count == 0) return NetworkNodeHealthState.Learning;
        var confident = streams.Where(s => s.IsConfidentPeriodic && s.TimeoutMilliseconds.HasValue).ToArray();
        if (confident.Length == 0) return NetworkNodeHealthState.Active;
        if (confident.All(s => (captureEnd - s.LastSeen).TotalMilliseconds > s.TimeoutMilliseconds!.Value))
            return NetworkNodeHealthState.Missing;
        return confident.Any(s => (captureEnd - s.LastSeen).TotalMilliseconds > s.TimeoutMilliseconds!.Value * 0.7)
            ? NetworkNodeHealthState.Suspect
            : NetworkNodeHealthState.Active;
    }

    private static double GetMedian(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}
