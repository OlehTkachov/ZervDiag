using CraneCAN.Core.Live;
using CraneCAN.Core.Models;

internal static class NodeHealthTests
{
    public static void Run()
    {
        var origin = DateTimeOffset.UnixEpoch;
        var monitor = new NodeHealthMonitor(
            minimumIntervals: 5,
            minimumLearningDuration: TimeSpan.FromSeconds(1),
            minimumTimeout: TimeSpan.FromMilliseconds(100),
            maximumTimeout: TimeSpan.FromSeconds(2),
            maximumPeriodToArm: TimeSpan.FromMilliseconds(500));

        for (var index = 0; index <= 10; index++)
            monitor.Observe(Frame(origin.AddMilliseconds(index * 100), 0x123));

        Check(monitor.Summary is { TrackedNodes: 1, ArmedNodes: 1, TimedOutNodes: 0 },
            "Stable periodic CAN ID did not arm Node Health.");

        var timeout = monitor.Evaluate(origin.AddMilliseconds(1500)).Single();
        Check(timeout.Kind == NodeHealthEventKind.Timeout &&
              timeout.Id == 0x123 &&
              Math.Abs(timeout.ExpectedPeriod.TotalMilliseconds - 100) < 0.001 &&
              Math.Abs(timeout.Timeout.TotalMilliseconds - 500) < 0.001,
            "Node Health timeout was not detected at the expected threshold.");

        Check(monitor.Evaluate(origin.AddMilliseconds(1600)).Count == 0,
            "Node Health emitted the same timeout more than once.");

        var recovered = monitor.Observe(Frame(origin.AddMilliseconds(1700), 0x123));
        Check(recovered?.Kind == NodeHealthEventKind.Recovered &&
              monitor.Summary.TimedOutNodes == 0,
            "Node Health recovery was not reported.");

        var burst = new NodeHealthMonitor(
            minimumIntervals: 5,
            minimumLearningDuration: TimeSpan.FromSeconds(1));
        for (var index = 0; index < 20; index++)
            burst.Observe(Frame(origin.AddMilliseconds(index * 10), 0x200));
        Check(burst.Summary.ArmedNodes == 0,
            "Short event-driven burst was incorrectly treated as a periodic node.");

        var irregular = new NodeHealthMonitor(
            minimumIntervals: 5,
            minimumLearningDuration: TimeSpan.FromMilliseconds(500),
            maximumPeriodToArm: TimeSpan.FromSeconds(1));
        var times = new[] { 0, 20, 420, 450, 900, 930, 1400, 1420, 1900 };
        foreach (var milliseconds in times)
            irregular.Observe(Frame(origin.AddMilliseconds(milliseconds), 0x300));
        Check(irregular.Summary.ArmedNodes == 0,
            "Highly irregular CAN ID was incorrectly armed.");

        var formats = new NodeHealthMonitor(
            minimumIntervals: 2,
            minimumLearningDuration: TimeSpan.FromMilliseconds(200));
        for (var index = 0; index <= 3; index++)
        {
            formats.Observe(Frame(origin.AddMilliseconds(index * 100), 0x123, extended: false));
            formats.Observe(Frame(origin.AddMilliseconds(index * 100), 0x123, extended: true));
        }
        Check(formats.Summary.TrackedNodes == 2,
            "Standard and Extended identifiers with the same numeric ID were merged.");
    }

    private static CanFrame Frame(DateTimeOffset timestamp, uint id, bool extended = false) => new()
    {
        Timestamp = timestamp,
        Channel = 0,
        Id = id,
        IsExtended = extended,
        Data = [0x01],
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
