using CraneCAN.Core.Models;

namespace CraneCAN.Core.Network;

public static class PassiveNetworkDiscoveryAnalyzer
{
    public static CanNetworkSnapshot Analyze(
        IEnumerable<CanFrame> sourceFrames,
        string sourceReference = "",
        int? bitrate = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFrames);
        var frames = sourceFrames
            .Where(frame => frame.Protocol == BusProtocol.ClassicalCan &&
                            frame.Direction == CanDirection.Rx &&
                            !frame.IsError && !frame.IsRemote)
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

        if (frames.Length == 0)
        {
            var now = DateTimeOffset.UtcNow;
            return new CanNetworkSnapshot
            {
                WindowStart = now,
                WindowEnd = now,
                SourceReference = sourceReference,
                Bitrate = bitrate,
                ProtocolEstimate = NetworkProtocolEstimate.Unknown
            };
        }

        var streams = NetworkPeriodicityAnalyzer.Analyze(frames);
        var protocol = NetworkProtocolEstimator.Estimate(frames, streams);
        var events = new List<NetworkEvent>();
        var flows = J1939FlowAnalyzer.Build(frames, events);
        var nodes = new List<NetworkNodeSnapshot>();

        if (protocol.Estimate is NetworkProtocolEstimate.J1939Likely or NetworkProtocolEstimate.MixedOrGateway)
            nodes.AddRange(J1939NodeAnalyzer.Build(frames, streams, protocol, events));
        else
            nodes.AddRange(GenericExtendedNodeAnalyzer.Build(frames));

        if (protocol.Estimate is NetworkProtocolEstimate.CanopenLikely or NetworkProtocolEstimate.MixedOrGateway)
            nodes.AddRange(CanopenNodeAnalyzer.Build(frames, streams, protocol, events));

        events.AddRange(NetworkGapEventAnalyzer.Build(frames, streams, frames[^1].Timestamp));

        return new CanNetworkSnapshot
        {
            WindowStart = frames[0].Timestamp,
            WindowEnd = frames[^1].Timestamp,
            SourceReference = sourceReference,
            Bitrate = bitrate,
            ProtocolEstimate = protocol.Estimate,
            ProtocolConfidence = protocol.Confidence,
            ProtocolEvidence = protocol.Evidence,
            FrameCount = frames.LongLength,
            StandardFrameCount = frames.LongCount(frame => !frame.IsExtended),
            ExtendedFrameCount = frames.LongCount(frame => frame.IsExtended),
            Nodes = nodes.OrderBy(node => node.Protocol).ThenBy(node => node.Address).ToList(),
            Flows = flows.OrderBy(flow => flow.SourceAddress).ThenBy(flow => flow.DestinationAddress ?? byte.MaxValue).ToList(),
            PeriodicStreams = streams.ToList(),
            Events = events.OrderBy(item => item.Timestamp).ThenBy(item => item.Kind).ToList()
        };
    }
}
