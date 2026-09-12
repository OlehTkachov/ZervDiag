using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

internal static class CanopenProtocolScorer
{
    public static double Score(IReadOnlyList<CanFrame> frames, ICollection<NetworkEvidence> evidence)
    {
        var items = frames.Where(f => !f.IsExtended).Select(f => (Frame: f, Info: Parse(f))).Where(x => x.Info is not null).ToArray();
        var score = 0.0;
        var heartbeat = items.Count(x => x.Info!.Kind is CanopenObjectKind.Heartbeat or CanopenObjectKind.BootUp);
        if (heartbeat >= 3) { score += 0.55; evidence.Add(E("CANOPEN_HEARTBEAT", $"Heartbeat/Boot-up: {heartbeat}", 0.55)); }
        else if (heartbeat > 0) { score += 0.30; evidence.Add(E("CANOPEN_HEARTBEAT_WEAK", $"Heartbeat/Boot-up: {heartbeat}", 0.30)); }
        var boot = items.Count(x => x.Info!.Kind == CanopenObjectKind.BootUp);
        if (boot > 0) { score += 0.20; evidence.Add(E("CANOPEN_BOOTUP", $"Boot-up: {boot}", 0.20)); }
        var nmt = items.Count(x => x.Info!.Kind == CanopenObjectKind.Nmt);
        if (nmt > 0) { score += 0.15; evidence.Add(E("CANOPEN_NMT", $"NMT: {nmt}", 0.15)); }
        var coherent = items.Where(x => x.Info!.NodeId.HasValue).GroupBy(x => x.Info!.NodeId!.Value).Count(g => g.Select(x => x.Frame.Id).Distinct().Count() >= 2);
        if (coherent > 0) { score += 0.10; evidence.Add(E("CANOPEN_COHERENT_NODE", $"Node-ID в нескольких COB-ID: {coherent}", 0.10)); }
        return Math.Clamp(score, 0, 1);
    }

    private static CanopenFrameInfo? Parse(CanFrame frame) => CanopenPassiveParser.TryParse(frame, out var info) ? info : null;
    private static NetworkEvidence E(string code, string description, double weight) => new() { Code = code, Description = description, Weight = weight };
}
