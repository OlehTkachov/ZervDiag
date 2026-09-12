using CraneCAN.Core.Network;

namespace CraneCAN.App;

public partial class MainWindow
{
    private sealed record NetworkStreamRow(NetworkPeriodicStreamSnapshot Stream)
    {
        public string Id => Stream.Id.ToString(Stream.IsExtended ? "X8" : "X3");
        public string Format => Stream.IsExtended ? "Extended" : "Standard";
        public string Frames => Stream.FrameCount.ToString();
        public string Median => Stream.MedianPeriodMilliseconds?.ToString("0.###") ?? "-";
        public string Jitter => Stream.JitterPercent.HasValue ? Stream.JitterPercent.Value.ToString("0.#") + " %" : "-";
        public string Timeout => Stream.TimeoutMilliseconds?.ToString("0.###") ?? "-";
        public string Classification => Stream.IsCanopenHeartbeat
            ? "CANopen Heartbeat"
            : Stream.IsConfidentPeriodic ? "periodic confirmed"
            : Stream.IsPeriodic ? "periodic candidate"
            : "event / unknown";
        public string LastSeen => Stream.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
}
