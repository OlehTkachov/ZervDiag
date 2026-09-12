namespace CraneCAN.Core.Network;

internal static class J1939NodeEvents
{
    public static void Add(J1939NodeContext context, ICollection<NetworkEvent> events)
    {
        var sa = context.Node.SourceAddress;
        var key = $"J1939:{sa:X2}";
        events.Add(new NetworkEvent
        {
            Kind = NetworkEventKind.NodeAppeared,
            Timestamp = context.Node.Frames[0].Frame.Timestamp,
            NodeKey = key,
            Id = context.Node.Frames[0].Frame.Id,
            IsExtended = true,
            Description = $"SA 0x{sa:X2} first seen"
        });

        foreach (var claim in context.Claims)
            events.Add(new NetworkEvent
            {
                Kind = sa == J1939PassiveParser.CannotClaimSourceAddress ? NetworkEventKind.AddressCannotClaim : NetworkEventKind.AddressClaim,
                Timestamp = claim.Frame.Timestamp,
                NodeKey = key,
                Id = claim.Frame.Id,
                IsExtended = true,
                Description = sa == J1939PassiveParser.CannotClaimSourceAddress ? "Address Cannot Claim" : $"Address Claim SA 0x{sa:X2}"
            });

        if (context.DistinctNames.Length > 1)
            events.Add(new NetworkEvent
            {
                Kind = NetworkEventKind.AddressConflict,
                Timestamp = context.Claims[^1].Frame.Timestamp,
                NodeKey = key,
                Description = $"Different NAME values on SA 0x{sa:X2}: {context.DistinctNames.Length}"
            });
    }
}
