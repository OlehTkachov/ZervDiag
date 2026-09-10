using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Core.Analysis;

public sealed record J1939TraceSignalSummary(
    Guid SignalId,
    string SignalName,
    SignalKnowledgeState Confidence,
    uint CanonicalCanId,
    int Pgn,
    int? Spn,
    int MatchingFrameCount,
    int DecodedFrameCount,
    int ShortFrameCount,
    IReadOnlyList<byte> SourceAddresses,
    double? MinimumEngineeringValue,
    double? MaximumEngineeringValue,
    double? LatestEngineeringValue,
    string Unit,
    DateTimeOffset? FirstTimestamp,
    DateTimeOffset? LastTimestamp);

public sealed record J1939TraceSignalAnalysisResult(
    int ExtendedRxFrameCount,
    int FramesMatchingProfile,
    IReadOnlyList<J1939TraceSignalSummary> Signals,
    IReadOnlyList<string> Warnings)
{
    public int ObservedSignalCount => Signals.Count(signal => signal.DecodedFrameCount > 0);
}

/// <summary>
/// Passive J1939 decoder for an already captured Classical CAN frame set.
/// Matching is performed by PGN, while PDU1 destination address remains fixed.
/// Source Address may vary between frames. No CAN transmit path exists here.
/// </summary>
public static class J1939TraceSignalAnalyzer
{
    public static J1939TraceSignalAnalysisResult Analyze(
        IReadOnlyList<CanFrame> frames,
        MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(profile);

        var warnings = new List<string>();
        var signals = profile.KnownSignals
            .Concat(profile.ExperimentalSignals)
            .Where(signal =>
                signal.Confidence != SignalKnowledgeState.Rejected &&
                IsJ1939Signal(signal))
            .GroupBy(signal => signal.SignalId)
            .Select(group => group.First())
            .ToArray();

        var states = new Dictionary<Guid, AggregateState>();
        foreach (var signal in signals)
        {
            try
            {
                MachineSignalDecoder.ValidateDefinition(signal);
                states.Add(signal.SignalId, new AggregateState(signal));
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or OverflowException)
            {
                warnings.Add(
                    $"J1939 signal «{signal.Name}» пропущен: {exception.Message}");
            }
        }

        if (states.Count == 0)
        {
            warnings.Add(
                "В текущем Machine Profile нет валидных J1939 PGN/SPN сигналов для декодирования TRC.");
            return new J1939TraceSignalAnalysisResult(0, 0, [], warnings);
        }

        var byPgn = states.Values
            .GroupBy(state => state.Signal.J1939Pgn!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var extendedRxFrames = 0;
        var matchedFrames = 0;

        foreach (var frame in frames)
        {
            if (!IsUsableExtendedFrame(frame))
                continue;

            extendedRxFrames++;
            J1939Identifier identifier;
            try
            {
                identifier = J1939IdentifierCodec.Decode(frame.Id);
            }
            catch
            {
                continue;
            }

            if (!byPgn.TryGetValue(identifier.Pgn, out var candidates))
                continue;

            var frameMatched = false;
            foreach (var state in candidates)
            {
                if (!J1939SignalDecoder.MatchesIdentifier(
                        state.Signal,
                        frame.Id,
                        frame.IsExtended))
                {
                    continue;
                }

                frameMatched = true;
                state.MatchingFrameCount++;
                state.SourceAddresses.Add(identifier.SourceAddress);

                int requiredLength;
                try
                {
                    requiredLength = MachineSignalDecoder.RequiredDataLength(state.Signal);
                }
                catch
                {
                    continue;
                }

                if (frame.Data.Length < requiredLength)
                {
                    state.ShortFrameCount++;
                    continue;
                }

                try
                {
                    var decoded = J1939SignalDecoder.Decode(state.Signal, frame);
                    state.AddDecoded(frame.Timestamp, decoded.EngineeringValue);
                }
                catch (Exception exception)
                    when (exception is ArgumentException or NotSupportedException or OverflowException)
                {
                    warnings.Add(
                        $"PGN 0x{identifier.Pgn:X5}" +
                        (state.Signal.J1939Spn.HasValue
                            ? $" / SPN {state.Signal.J1939Spn.Value}"
                            : string.Empty) +
                        $" не декодирован в одном кадре: {exception.Message}");
                }
            }

            if (frameMatched)
                matchedFrames++;
        }

        var summaries = states.Values
            .Select(state => state.ToSummary())
            .OrderBy(summary => summary.Pgn)
            .ThenBy(summary => summary.Spn ?? int.MaxValue)
            .ThenBy(summary => summary.SignalName, StringComparer.Ordinal)
            .ToArray();

        if (summaries.All(summary => summary.DecodedFrameCount == 0))
        {
            warnings.Add(
                "J1939 PGN/SPN сигналы есть в Machine Profile, но в выбранной трассе ни один не был декодирован.");
        }

        if (summaries.Any(summary => summary.ShortFrameCount > 0))
        {
            warnings.Add(
                "Часть J1939 кадров имела DATA короче полного поля сигнала и была пропущена для engineering decode.");
        }

        return new J1939TraceSignalAnalysisResult(
            extendedRxFrames,
            matchedFrames,
            summaries,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static bool IsJ1939Signal(MachineSignal signal) =>
        signal.IsExtended &&
        signal.J1939Pgn.HasValue &&
        string.Equals(
            signal.Protocol,
            "J1939",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUsableExtendedFrame(CanFrame frame) =>
        frame.Protocol == BusProtocol.ClassicalCan &&
        frame.Direction == CanDirection.Rx &&
        frame.IsExtended &&
        !frame.IsRemote &&
        !frame.IsError &&
        frame.Id <= 0x1FFFFFFFu;

    private sealed class AggregateState
    {
        public AggregateState(MachineSignal signal) => Signal = signal;

        public MachineSignal Signal { get; }
        public int MatchingFrameCount { get; set; }
        public int ShortFrameCount { get; set; }
        public int DecodedFrameCount { get; private set; }
        public HashSet<byte> SourceAddresses { get; } = [];
        public double? Minimum { get; private set; }
        public double? Maximum { get; private set; }
        public double? Latest { get; private set; }
        public DateTimeOffset? FirstTimestamp { get; private set; }
        public DateTimeOffset? LastTimestamp { get; private set; }

        public void AddDecoded(DateTimeOffset timestamp, double value)
        {
            DecodedFrameCount++;
            Minimum = !Minimum.HasValue || value < Minimum.Value ? value : Minimum;
            Maximum = !Maximum.HasValue || value > Maximum.Value ? value : Maximum;

            if (!FirstTimestamp.HasValue || timestamp < FirstTimestamp.Value)
                FirstTimestamp = timestamp;
            if (!LastTimestamp.HasValue || timestamp >= LastTimestamp.Value)
            {
                LastTimestamp = timestamp;
                Latest = value;
            }
        }

        public J1939TraceSignalSummary ToSummary() =>
            new(
                Signal.SignalId,
                Signal.Name,
                Signal.Confidence,
                Signal.CanId,
                Signal.J1939Pgn!.Value,
                Signal.J1939Spn,
                MatchingFrameCount,
                DecodedFrameCount,
                ShortFrameCount,
                SourceAddresses.OrderBy(value => value).ToArray(),
                Minimum,
                Maximum,
                Latest,
                Signal.Unit,
                FirstTimestamp,
                LastTimestamp);
    }
}
