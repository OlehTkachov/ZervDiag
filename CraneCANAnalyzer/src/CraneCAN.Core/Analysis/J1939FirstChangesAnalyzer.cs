using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Core.Analysis;

public sealed record J1939FirstChangeCandidate(
    int Pgn,
    int? DestinationAddress,
    int? DataIndex,
    IncidentTransitionKind Kind,
    IncidentTransitionPriority Priority,
    double ReactionMilliseconds,
    string BaselineValue,
    string ObservedValue,
    double BaselineAgreementPercent,
    int ConfirmationCount,
    IReadOnlyList<int> BaselineSourceAddresses,
    IReadOnlyList<int> SearchSourceAddresses,
    IReadOnlyList<uint> BaselineRawIds,
    IReadOnlyList<uint> SearchRawIds,
    string Description)
{
    public string PgnText => $"0x{Pgn:X5}";
}

public sealed record J1939SourceAddressChange(
    int Pgn,
    int? DestinationAddress,
    IReadOnlyList<int> BaselineSourceAddresses,
    IReadOnlyList<int> SearchSourceAddresses,
    IReadOnlyList<uint> BaselineRawIds,
    IReadOnlyList<uint> SearchRawIds)
{
    public string PgnText => $"0x{Pgn:X5}";
}

public sealed record J1939FirstChangesResult(
    IncidentTransitionAnalysisResult RawExact,
    IReadOnlyList<J1939FirstChangeCandidate> Candidates,
    IReadOnlyList<J1939SourceAddressChange> SourceAddressChanges,
    int ExtendedBaselineFrameCount,
    int ExtendedSearchFrameCount,
    int SuppressedExactIdLifecycleCandidates,
    IReadOnlyList<string> Warnings)
{
    public int HighCount =>
        Candidates.Count(candidate =>
            candidate.Priority == IncidentTransitionPriority.High);

    public int MediumCount =>
        Candidates.Count(candidate =>
            candidate.Priority == IncidentTransitionPriority.Medium);

    public int InfoCount =>
        Candidates.Count(candidate =>
            candidate.Priority == IncidentTransitionPriority.Info);

    public J1939FirstChangeCandidate? Earliest =>
        Candidates.FirstOrDefault();
}

/// <summary>
/// Parallel J1939 view of Incident First Changes.
///
/// The established raw analyzer remains authoritative for exact CAN IDs.
/// This view removes Priority and Source Address from 29-bit J1939 identity,
/// while retaining PGN and (for PDU1) Destination Address. This prevents an
/// SA-only change from being reported as "old ID stopped / new ID appeared".
/// No CAN transmission is performed.
/// </summary>
public static class J1939FirstChangesAnalyzer
{
    public static J1939FirstChangesResult Analyze(
        PreFaultIncident incident,
        IncidentTransitionAnalysisResult? rawExact = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var raw = rawExact ?? IncidentTransitionAnalyzer.Analyze(incident);
        if (incident.Markers.Count == 0)
            throw new InvalidOperationException(
                "В incident отсутствует отметка события.");

        var extendedFrames = incident.Frames
            .Where(IsUsableExtendedFrame)
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

        var baselineOriginal = extendedFrames
            .Where(frame =>
                frame.Timestamp >= raw.BaselineStart &&
                frame.Timestamp < raw.BaselineEnd)
            .ToArray();
        var searchOriginal = extendedFrames
            .Where(frame =>
                frame.Timestamp >= raw.SearchStart &&
                frame.Timestamp < raw.SearchEnd)
            .ToArray();

        var warnings = raw.Warnings.ToList();
        warnings.Add(
            "J1939 PGN-view интерпретирует каждый обычный Rx 29-bit Classical CAN frame как J1939. " +
            "Сам 29-bit CAN ID не доказывает, что шина действительно J1939; для проверки сохранён исходный exact-ID First Changes.");

        if (baselineOriginal.Length == 0)
        {
            warnings.Add(
                "В baseline-окне нет Rx 29-bit кадров: J1939 PGN-normalized First Changes не выполнялся.");

            return new J1939FirstChangesResult(
                raw,
                [],
                [],
                0,
                searchOriginal.Length,
                0,
                warnings.Distinct(StringComparer.Ordinal).ToArray());
        }

        var normalizedFrames = extendedFrames
            .Select(frame => frame with
            {
                Id = NormalizeMessageKeyCanId(frame.Id)
            })
            .ToArray();

        var normalizedIncident = incident with
        {
            Frames = normalizedFrames
        };

        var normalized =
            IncidentTransitionAnalyzer.Analyze(normalizedIncident);

        var candidates = normalized.Candidates
            .Select(candidate =>
                BuildCandidate(
                    incident,
                    raw,
                    candidate))
            .ToArray();

        var sourceAddressChanges =
            BuildSourceAddressChanges(
                baselineOriginal,
                searchOriginal);

        var normalizedLifecycle = candidates
            .Where(candidate =>
                IsLifecycle(candidate.Kind))
            .Select(candidate =>
                (new MessageKey(
                    candidate.Pgn,
                    candidate.DestinationAddress),
                 candidate.Kind))
            .ToHashSet();

        var suppressedLifecycle =
            raw.Candidates.Count(candidate =>
            {
                if (!candidate.IsExtended ||
                    !IsLifecycle(candidate.Kind))
                {
                    return false;
                }

                var key = DecodeKey(candidate.Id);
                return !normalizedLifecycle.Contains(
                    (key, candidate.Kind));
            });

        if (suppressedLifecycle > 0)
        {
            warnings.Add(
                $"J1939 PGN-normalization suppressed {suppressedLifecycle} exact-ID lifecycle candidate(s), " +
                "которые объясняются изменением Priority/Source Address при сохранении того же PGN/DA.");
        }

        if (sourceAddressChanges.Count > 0)
        {
            warnings.Add(
                $"В {sourceAddressChanges.Count} PGN/DA key(s) набор Source Address отличается между BASELINE и SEARCH. " +
                "Это показано как metadata и само по себе не считается появлением/исчезновением PGN.");
        }

        var multiSenderKeys = baselineOriginal
            .Concat(searchOriginal)
            .GroupBy(frame => DecodeKey(frame.Id))
            .Where(group => group
                .Select(frame =>
                    J1939IdentifierCodec
                        .Decode(frame.Id)
                        .SourceAddress)
                .Distinct()
                .Skip(1)
                .Any())
            .Select(group => group.Key)
            .Distinct()
            .Count();

        if (multiSenderKeys > 0)
        {
            warnings.Add(
                $"В {multiSenderKeys} PGN/DA key(s) наблюдалось несколько Source Address. " +
                "Byte/DLC-анализ PGN-view объединяет эти отправители намеренно; при разных payload semantics проверяйте raw exact-ID view.");
        }

        warnings.Add(
            "Для PDU1 Destination Address остаётся частью normalized message key; смена DA не подавляется.");

        return new J1939FirstChangesResult(
            raw,
            candidates,
            sourceAddressChanges,
            baselineOriginal.Length,
            searchOriginal.Length,
            suppressedLifecycle,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Returns a deterministic 29-bit identifier used only as an internal
    /// analysis key. Priority is fixed to 6, Source Address to 0. PGN is
    /// retained; for PDU1 the Destination Address is retained as well.
    /// </summary>
    public static uint NormalizeMessageKeyCanId(uint canId)
    {
        var identifier =
            J1939IdentifierCodec.Decode(canId);

        uint normalized = 6u << 26;
        if (identifier.ExtendedDataPage)
            normalized |= 1u << 25;
        if (identifier.DataPage)
            normalized |= 1u << 24;

        normalized |=
            checked((uint)identifier.PduFormat) << 16;

        var ps = identifier.IsPdu1
            ? identifier.DestinationAddress!.Value
            : identifier.PduSpecific;
        normalized |= checked((uint)ps) << 8;

        // Source Address intentionally normalized to 0x00.
        return normalized;
    }

    private static J1939FirstChangeCandidate BuildCandidate(
        PreFaultIncident incident,
        IncidentTransitionAnalysisResult raw,
        IncidentTransitionCandidate normalizedCandidate)
    {
        var key = DecodeKey(normalizedCandidate.Id);
        var baselineFrames = FramesForKey(
            incident,
            raw.BaselineStart,
            raw.BaselineEnd,
            key);
        var searchFrames = FramesForKey(
            incident,
            raw.SearchStart,
            raw.SearchEnd,
            key);

        return new J1939FirstChangeCandidate(
            key.Pgn,
            key.DestinationAddress,
            normalizedCandidate.DataIndex,
            normalizedCandidate.Kind,
            normalizedCandidate.Priority,
            normalizedCandidate.ReactionMilliseconds,
            normalizedCandidate.BaselineValue,
            normalizedCandidate.ObservedValue,
            normalizedCandidate.BaselineAgreementPercent,
            normalizedCandidate.ConfirmationCount,
            SourceAddresses(baselineFrames),
            SourceAddresses(searchFrames),
            RawIds(baselineFrames),
            RawIds(searchFrames),
            normalizedCandidate.Description +
            " J1939 PGN-view исключает Priority/Source Address из message identity; " +
            "для PDU1 Destination Address сохранён.");
    }

    private static IReadOnlyList<J1939SourceAddressChange>
        BuildSourceAddressChanges(
            IReadOnlyList<CanFrame> baseline,
            IReadOnlyList<CanFrame> search)
    {
        var baselineByKey = baseline
            .GroupBy(frame => DecodeKey(frame.Id))
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
        var searchByKey = search
            .GroupBy(frame => DecodeKey(frame.Id))
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var result = new List<J1939SourceAddressChange>();
        foreach (var key in baselineByKey.Keys
                     .Intersect(searchByKey.Keys)
                     .OrderBy(key => key.Pgn)
                     .ThenBy(key =>
                         key.DestinationAddress ?? -1))
        {
            var baselineFrames =
                baselineByKey[key];
            var searchFrames =
                searchByKey[key];
            var baselineSources =
                SourceAddresses(baselineFrames);
            var searchSources =
                SourceAddresses(searchFrames);

            if (baselineSources.SequenceEqual(searchSources))
                continue;

            result.Add(
                new J1939SourceAddressChange(
                    key.Pgn,
                    key.DestinationAddress,
                    baselineSources,
                    searchSources,
                    RawIds(baselineFrames),
                    RawIds(searchFrames)));
        }

        return result;
    }

    private static CanFrame[] FramesForKey(
        PreFaultIncident incident,
        DateTimeOffset start,
        DateTimeOffset end,
        MessageKey key) =>
        incident.Frames
            .Where(frame =>
                frame.Timestamp >= start &&
                frame.Timestamp < end &&
                IsUsableExtendedFrame(frame) &&
                DecodeKey(frame.Id) == key)
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

    private static int[] SourceAddresses(
        IEnumerable<CanFrame> frames) =>
        frames
            .Select(frame =>
                J1939IdentifierCodec
                    .Decode(frame.Id)
                    .SourceAddress)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

    private static uint[] RawIds(
        IEnumerable<CanFrame> frames) =>
        frames
            .Select(frame => frame.Id)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

    private static MessageKey DecodeKey(uint canId)
    {
        var identifier =
            J1939IdentifierCodec.Decode(canId);
        return new MessageKey(
            identifier.Pgn,
            identifier.IsPdu1
                ? identifier.DestinationAddress
                : null);
    }

    private static bool IsLifecycle(
        IncidentTransitionKind kind) =>
        kind is
            IncidentTransitionKind.IdAppeared or
            IncidentTransitionKind.PeriodicIdStopped;

    private static bool IsUsableExtendedFrame(
        CanFrame frame) =>
        frame.Protocol ==
            BusProtocol.ClassicalCan &&
        frame.Direction ==
            CanDirection.Rx &&
        frame.IsExtended &&
        !frame.IsRemote &&
        !frame.IsError &&
        frame.Id <=
            J1939IdentifierCodec.MaximumExtendedCanId;

    private readonly record struct MessageKey(
        int Pgn,
        int? DestinationAddress);
}
