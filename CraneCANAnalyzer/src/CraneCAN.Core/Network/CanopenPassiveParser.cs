using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

public enum CanopenObjectKind
{
    Unknown,
    Nmt,
    Sync,
    Emcy,
    Tpdo1,
    Rpdo1,
    Tpdo2,
    Rpdo2,
    Tpdo3,
    Rpdo3,
    Tpdo4,
    Rpdo4,
    SdoResponse,
    SdoRequest,
    Heartbeat,
    BootUp
}

public sealed record CanopenFrameInfo
{
    public uint CobId { get; init; }
    public CanopenObjectKind Kind { get; init; }
    public byte? NodeId { get; init; }
    public byte? NmtState { get; init; }
    public string NmtStateText { get; init; } = string.Empty;
    public bool IsStrongEvidence { get; init; }
}

public static class CanopenPassiveParser
{
    public static bool TryParse(CanFrame frame, out CanopenFrameInfo info)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Protocol != BusProtocol.ClassicalCan || frame.IsExtended || frame.IsRemote || frame.IsError)
        {
            info = default!;
            return false;
        }

        var id = frame.Id;
        if (id == 0x000)
        {
            info = new CanopenFrameInfo
            {
                CobId = id,
                Kind = CanopenObjectKind.Nmt,
                NodeId = frame.Data.Length >= 2 && frame.Data[1] is >= 1 and <= 127 ? frame.Data[1] : null,
                IsStrongEvidence = frame.Data.Length >= 2
            };
            return true;
        }

        if (id == 0x080)
        {
            info = new CanopenFrameInfo { CobId = id, Kind = CanopenObjectKind.Sync };
            return true;
        }

        if (id is >= 0x701 and <= 0x77F && frame.Data.Length >= 1)
        {
            var node = (byte)(id - 0x700);
            var state = frame.Data[0];
            if (state is 0x00 or 0x04 or 0x05 or 0x7F)
            {
                info = new CanopenFrameInfo
                {
                    CobId = id,
                    Kind = state == 0x00 ? CanopenObjectKind.BootUp : CanopenObjectKind.Heartbeat,
                    NodeId = node,
                    NmtState = state,
                    NmtStateText = DecodeNmtState(state),
                    IsStrongEvidence = true
                };
                return true;
            }
        }

        if (TryNodeRange(id, 0x080, out var emcyNode)) { info = Create(id, CanopenObjectKind.Emcy, emcyNode); return true; }
        if (TryNodeRange(id, 0x180, out var n1)) { info = Create(id, CanopenObjectKind.Tpdo1, n1); return true; }
        if (TryNodeRange(id, 0x200, out var n2)) { info = Create(id, CanopenObjectKind.Rpdo1, n2); return true; }
        if (TryNodeRange(id, 0x280, out var n3)) { info = Create(id, CanopenObjectKind.Tpdo2, n3); return true; }
        if (TryNodeRange(id, 0x300, out var n4)) { info = Create(id, CanopenObjectKind.Rpdo2, n4); return true; }
        if (TryNodeRange(id, 0x380, out var n5)) { info = Create(id, CanopenObjectKind.Tpdo3, n5); return true; }
        if (TryNodeRange(id, 0x400, out var n6)) { info = Create(id, CanopenObjectKind.Rpdo3, n6); return true; }
        if (TryNodeRange(id, 0x480, out var n7)) { info = Create(id, CanopenObjectKind.Tpdo4, n7); return true; }
        if (TryNodeRange(id, 0x500, out var n8)) { info = Create(id, CanopenObjectKind.Rpdo4, n8); return true; }
        if (TryNodeRange(id, 0x580, out var n9)) { info = Create(id, CanopenObjectKind.SdoResponse, n9); return true; }
        if (TryNodeRange(id, 0x600, out var n10)) { info = Create(id, CanopenObjectKind.SdoRequest, n10); return true; }

        info = default!;
        return false;
    }

    public static string DecodeNmtState(byte value) => value switch
    {
        0x00 => "Boot-up",
        0x04 => "Stopped",
        0x05 => "Operational",
        0x7F => "Pre-operational",
        _ => $"0x{value:X2}"
    };

    private static bool TryNodeRange(uint id, uint baseId, out byte nodeId)
    {
        if (id > baseId && id - baseId <= 127)
        {
            nodeId = (byte)(id - baseId);
            return true;
        }
        nodeId = 0;
        return false;
    }

    private static CanopenFrameInfo Create(uint id, CanopenObjectKind kind, byte nodeId) => new()
    {
        CobId = id,
        Kind = kind,
        NodeId = nodeId
    };
}
