using CraneCAN.Core.Network;

namespace CraneCAN.App;

public partial class MainWindow
{
    private sealed record NetworkFlowRow(NetworkFlowSnapshot Flow)
    {
        public string Source => "0x" + Flow.SourceAddress.ToString("X2");
        public string Destination => Flow.DestinationAddress.HasValue
            ? "0x" + Flow.DestinationAddress.Value.ToString("X2")
            : "PDU2";
        public string Kind => Flow.Kind;
        public string Pgns => Flow.Pgns.Count == 0 ? "-" : string.Join(", ", Flow.Pgns);
        public string Frames => Flow.FrameCount.ToString();
        public string Frequency => Flow.AverageFrequencyHertz.ToString("0.###");
        public string FirstSeen => Flow.FirstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        public string LastSeen => Flow.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    }
}
