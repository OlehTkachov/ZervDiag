using CraneCAN.Core.Network;

namespace CraneCAN.App;

public partial class MainWindow
{
    private sealed record NetworkEventRow(NetworkEvent Event)
    {
        public string Timestamp => Event.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        public string Kind => Event.Kind.ToString();
        public string Node => string.IsNullOrWhiteSpace(Event.NodeKey) ? "-" : Event.NodeKey;
        public string Id => Event.Id.HasValue
            ? Event.Id.Value.ToString(Event.IsExtended == true ? "X8" : "X3")
            : "-";
        public string Description => Event.Description;
    }
}
