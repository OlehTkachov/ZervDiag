using System.Globalization;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Core.Analysis;

public sealed record J1939IncidentStepAnnotation(
    int Sequence,
    int Pgn,
    IReadOnlyList<int> Spns,
    byte ObservedSourceAddress,
    byte? DestinationAddress,
    string ProtocolText,
    string EngineeringTransition);

public sealed record J1939IncidentEventChainResult(
    IncidentEventChainResult Chain,
    IReadOnlyDictionary<int, J1939IncidentStepAnnotation> Annotations);

public sealed record J1939IncidentProfileTimelineResult(
    IncidentProfileSignalTimelineResult Timeline,
    int Pgn,
    int? Spn,
    IReadOnlyList<byte> SourceAddresses);

/// <summary>
/// Adds PGN/SPN-aware Machine Profile interpretation to an already observed
/// incident chain. It never changes the raw IncidentTransition candidates and
/// never transmits CAN frames.
/// </summary>
public static class J1939IncidentAnalyzer
{
    private const double CandidateFrameToleranceMilliseconds = 5.0;

    public static J1939IncidentEventChainResult EnrichEventChain(
        IncidentEventChainResult chain,
        IncidentTransitionAnalysisResult transition,
        PreFaultIncident incident,
        MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(profile);

        var signals = profile.KnownSignals
            .Concat(profile.ExperimentalSignals)
            .Where(signal =>
                signal.Confidence != SignalKnowledgeState.Rejected &&
                J1939TraceSignalAnalyzer.IsJ1939Signal(signal) &&
                IsValidSignal(signal))
            .GroupBy(signal => signal.SignalId)
            .Select(group => group.First())
            .ToArray();

        if (signals.Length == 0 || chain.Steps.Count == 0)
        {
            return new J1939IncidentEventChainResult(
                chain,
                new Dictionary<int, J1939IncidentStepAnnotation>());
        }

        var annotations = new Dictionary<int, J1939IncidentStepAnnotation>();
        var enrichedSteps = new List<IncidentEventChainStep>(chain.Steps.Count);

        foreach (var step in chain.Steps)
        {
            var matches = signals
                .Where(signal => MatchesStep(signal, step))
                .OrderByDescending(signal => ConfidenceRank(signal.Confidence))
                .ThenBy(signal => signal.J1939Spn ?? int.MaxValue)
                .ThenBy(signal => signal.Name, StringComparer.Ordinal)
                .ToArray();

            if (matches.Length == 0)
            {
                enrichedSteps.Add(step);
                continue;
            }

            var observedIdentifier = J1939IdentifierCodec.Decode(step.Id);
            var spns = matches
                .Where(signal => signal.J1939Spn.HasValue)
                .Select(signal => signal.J1939Spn!.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();

            var profileNames = step.ProfileSignals
                .Concat(matches.Select(signal =>
                    string.IsNullOrWhiteSpace(signal.Name)
                        ? $"signal {signal.SignalId:N}"
                        : signal.Name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var highest = HighestConfidence(
                step.HighestSignalConfidence,
                matches.Select(signal => signal.Confidence));

            var engineering = BuildEngineeringTransition(
                transition,
                incident,
                step,
                matches);
            var protocolText = BuildProtocolText(
                observedIdentifier,
                spns);

            annotations.Add(
                step.Sequence,
                new J1939IncidentStepAnnotation(
                    step.Sequence,
                    observedIdentifier.Pgn,
                    spns,
                    observedIdentifier.SourceAddress,
                    observedIdentifier.DestinationAddress,
                    protocolText,
                    engineering));

            enrichedSteps.Add(step with
            {
                ProfileSignals = profileNames,
                HighestSignalConfidence = highest
            });
        }

        var warnings = chain.Warnings
            .Where(warning => !warning.StartsWith(
                "Ни один шаг цепочки не сопоставился с сигналами текущего Machine Profile.",
                StringComparison.Ordinal))
            .Where(warning => !warning.StartsWith(
                "Аннотация Machine Profile означает только пересечение CAN ID/байтов",
                StringComparison.Ordinal))
            .ToList();

        if (annotations.Count > 0)
        {
            warnings.Add(
                "J1939 Machine Profile сопоставляется по PGN, поэтому Source Address может отличаться от canonical ID профиля. " +
                "Для PDU1 Destination Address остаётся фиксированным. PGN/SPN-аннотация и engineering value являются интерпретацией профиля, а не доказательством физической причинности.");
        }

        var enrichedChain = new IncidentEventChainResult(
            enrichedSteps,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
        return new J1939IncidentEventChainResult(
            enrichedChain,
            annotations);
    }

    public static J1939IncidentProfileTimelineResult AnalyzeProfileTimeline(
        PreFaultIncident incident,
        IncidentEventChainStep step,
        MachineSignal signal,
        int maximumRenderedPoints = 3000)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(signal);

        if (!J1939TraceSignalAnalyzer.IsJ1939Signal(signal))
        {
            throw new ArgumentException(
                "Signal не содержит явной J1939 PGN metadata.",
                nameof(signal));
        }

        if (!MatchesStep(signal, step))
        {
            throw new ArgumentException(
                $"Observed ID 0x{step.Id:X8} не соответствует J1939 PGN 0x{signal.J1939Pgn!.Value:X5} выбранного signal.",
                nameof(step));
        }

        var matched = incident.Frames
            .Where(frame => IsUsableExtendedFrame(frame) &&
                            J1939SignalDecoder.MatchesIdentifier(
                                signal,
                                frame.Id,
                                frame.IsExtended))
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

        if (matched.Length == 0)
        {
            throw new InvalidOperationException(
                $"В incident нет Rx кадров J1939 PGN 0x{signal.J1939Pgn!.Value:X5} для signal «{signal.Name}».");
        }

        // Reuse the established profile-timeline decoder/downsampler by
        // normalizing only the identifier of the already matched frame copy.
        // Original incident frames are never changed.
        var normalizedFrames = matched
            .Select(frame => frame with { Id = signal.CanId })
            .ToArray();
        var normalizedIncident = incident with { Frames = normalizedFrames };
        var normalizedStep = step with
        {
            Id = signal.CanId,
            IsExtended = true
        };

        var timeline = IncidentProfileSignalTimelineAnalyzer.Analyze(
            normalizedIncident,
            normalizedStep,
            signal,
            maximumRenderedPoints);
        var sourceAddresses = matched
            .Select(frame => J1939IdentifierCodec.Decode(frame.Id).SourceAddress)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        var warnings = timeline.Warnings.ToList();
        if (sourceAddresses.Length > 1)
        {
            warnings.Add(
                $"J1939 PGN 0x{signal.J1939Pgn!.Value:X5} наблюдался от нескольких Source Address: " +
                string.Join(", ", sourceAddresses.Select(value => $"0x{value:X2}")) +
                ". Все они объединены только потому, что PGN совпадает; для PDU1 Destination Address также проверен.");
        }

        timeline = timeline with
        {
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };

        return new J1939IncidentProfileTimelineResult(
            timeline,
            signal.J1939Pgn!.Value,
            signal.J1939Spn,
            sourceAddresses);
    }

    public static bool MatchesStep(
        MachineSignal signal,
        IncidentEventChainStep step)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(step);

        if (!J1939TraceSignalAnalyzer.IsJ1939Signal(signal) ||
            !step.IsExtended ||
            !J1939SignalDecoder.MatchesIdentifier(
                signal,
                step.Id,
                step.IsExtended))
        {
            return false;
        }

        if (!step.DataIndex.HasValue)
            return true;

        try
        {
            return MachineSignalDecoder.CoversDataByte(
                signal,
                step.DataIndex.Value);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildProtocolText(
        J1939Identifier identifier,
        IReadOnlyList<int> spns)
    {
        var parts = new List<string>
        {
            $"PGN 0x{identifier.Pgn:X5}"
        };
        if (spns.Count == 1)
            parts.Add($"SPN {spns[0]}");
        else if (spns.Count > 1)
            parts.Add("SPN " + string.Join(",", spns));

        parts.Add($"SA 0x{identifier.SourceAddress:X2}");
        if (identifier.DestinationAddress.HasValue)
            parts.Add($"DA 0x{identifier.DestinationAddress.Value:X2}");
        return string.Join(" · ", parts);
    }

    private static string BuildEngineeringTransition(
        IncidentTransitionAnalysisResult transition,
        PreFaultIncident incident,
        IncidentEventChainStep step,
        IReadOnlyList<MachineSignal> matches)
    {
        if (step.Kind != IncidentTransitionKind.ByteChanged ||
            !step.DataIndex.HasValue)
        {
            return string.Empty;
        }

        var observed = FindObservedFrame(
            incident,
            transition.MarkerTime,
            step);
        if (observed is null)
            return string.Empty;

        var baseline = FindBaselineFrame(
            incident,
            transition,
            step);
        var values = new List<string>();

        foreach (var signal in matches)
        {
            if (!CanDecode(signal, observed))
                continue;

            try
            {
                var observedValue = J1939SignalDecoder
                    .Decode(signal, observed)
                    .EngineeringValue;
                var label = string.IsNullOrWhiteSpace(signal.Name)
                    ? "signal"
                    : signal.Name;
                if (signal.J1939Spn.HasValue)
                    label += $" [SPN {signal.J1939Spn.Value}]";

                var unit = string.IsNullOrWhiteSpace(signal.Unit)
                    ? string.Empty
                    : " " + signal.Unit.Trim();

                if (baseline is not null && CanDecode(signal, baseline))
                {
                    var baselineValue = J1939SignalDecoder
                        .Decode(signal, baseline)
                        .EngineeringValue;
                    values.Add(
                        $"{label}: {FormatNumber(baselineValue)} → {FormatNumber(observedValue)}{unit}");
                }
                else
                {
                    values.Add(
                        $"{label}: {FormatNumber(observedValue)}{unit}");
                }
            }
            catch
            {
                // A single undecodable profile field must not hide the raw step.
            }
        }

        return string.Join("; ", values.Distinct(StringComparer.Ordinal));
    }

    private static CanFrame? FindObservedFrame(
        PreFaultIncident incident,
        DateTimeOffset marker,
        IncidentEventChainStep step)
    {
        var target = marker.AddMilliseconds(step.ReactionMilliseconds);
        var candidates = incident.Frames
            .Where(frame =>
                IsUsableExtendedFrame(frame) &&
                frame.Id == step.Id &&
                frame.IsExtended == step.IsExtended)
            .Select(frame => new
            {
                Frame = frame,
                Distance = Math.Abs((frame.Timestamp - target).TotalMilliseconds)
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Frame.Timestamp)
            .FirstOrDefault();

        return candidates is not null &&
               candidates.Distance <= CandidateFrameToleranceMilliseconds
            ? candidates.Frame
            : null;
    }

    private static CanFrame? FindBaselineFrame(
        PreFaultIncident incident,
        IncidentTransitionAnalysisResult transition,
        IncidentEventChainStep step)
    {
        byte? expectedByte = null;
        if (step.DataIndex.HasValue &&
            step.BaselineValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            byte.TryParse(
                step.BaselineValue.AsSpan(2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            expectedByte = parsed;
        }

        return incident.Frames
            .Where(frame =>
                IsUsableExtendedFrame(frame) &&
                frame.Id == step.Id &&
                frame.IsExtended == step.IsExtended &&
                frame.Timestamp >= transition.BaselineStart &&
                frame.Timestamp < transition.BaselineEnd &&
                (!expectedByte.HasValue ||
                 !step.DataIndex.HasValue ||
                 (frame.Data.Length > step.DataIndex.Value &&
                  frame.Data[step.DataIndex.Value] == expectedByte.Value)))
            .OrderByDescending(frame => frame.Timestamp)
            .FirstOrDefault();
    }

    private static bool CanDecode(MachineSignal signal, CanFrame frame)
    {
        try
        {
            return frame.Data.Length >=
                   MachineSignalDecoder.RequiredDataLength(signal) &&
                   J1939SignalDecoder.MatchesIdentifier(
                       signal,
                       frame.Id,
                       frame.IsExtended);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidSignal(MachineSignal signal)
    {
        try
        {
            MachineSignalDecoder.ValidateDefinition(signal);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsableExtendedFrame(CanFrame frame) =>
        frame.Protocol == BusProtocol.ClassicalCan &&
        frame.Direction == CanDirection.Rx &&
        frame.IsExtended &&
        !frame.IsRemote &&
        !frame.IsError;

    private static SignalKnowledgeState? HighestConfidence(
        SignalKnowledgeState? existing,
        IEnumerable<SignalKnowledgeState> additional)
    {
        var values = additional
            .Select(value => (SignalKnowledgeState?)value)
            .ToList();
        if (existing.HasValue)
            values.Add(existing.Value);
        return values.Count == 0
            ? null
            : values.OrderByDescending(value => ConfidenceRank(value!.Value)).First();
    }

    private static int ConfidenceRank(SignalKnowledgeState state) => state switch
    {
        SignalKnowledgeState.Confirmed => 5,
        SignalKnowledgeState.Probable => 4,
        SignalKnowledgeState.Candidate => 3,
        SignalKnowledgeState.Unknown => 2,
        SignalKnowledgeState.Rejected => 1,
        _ => 0
    };

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
