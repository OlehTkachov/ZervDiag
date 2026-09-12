using CraneCAN.Core.Network;

namespace CraneCAN.App;

public partial class MainWindow
{
    private sealed record NetworkNodeRow(NetworkNodeSnapshot Node)
    {
        public string Protocol => Node.Protocol.ToString();
        public string Address => Node.AddressText;
        public string Identity => Node.Identity;
        public string State => Node.State;
        public string Health => Node.Health.ToString();
        public string Frames => Node.FrameCount.ToString();
        public string Frequency => Node.AverageFrequencyHertz.ToString("0.###");
        public string Periodicity => Node.PeriodicityQuality.ToString("0.#") + " %";
        public string Messages => Node.PgnOrCobIds.Count == 0 ? "-" : string.Join(", ", Node.PgnOrCobIds);
        public string Peers => Node.DirectedPeers.Count == 0 ? "-" : string.Join(", ", Node.DirectedPeers);
        public string Confidence => Node.Confidence.ToString("P0");
        public string Evidence => Node.Evidence.Count == 0 ? "-" : string.Join("; ", Node.Evidence);
    }
}
