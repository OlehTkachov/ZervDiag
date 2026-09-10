using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Analysis;

public sealed record IncidentProfileSignalTimelinePoint(
    double RelativeMilliseconds,
    double EngineeringValue,
    ulong RawUnsigned,
    long? RawSigned);

public sealed record IncidentProfileSignalTimelineResult(
    DateTimeOffset MarkerTime,
    MachineSignal Signal,
    double TransitionMilliseconds,
    int MatchingFrameCount,
    int ShortFrameCount,
    int SourceFrameCount,
    IReadOnlyList<IncidentProfileSignalTimelinePoint> RenderedPoints,
    double MinimumEngineeringValue,
    double MaximumEngineeringValue,
    double StartMilliseconds,
    double EndMilliseconds,
    IReadOnlyList<string> Warnings);

public static class IncidentProfileSignalTimelineAnalyzer
{
    public static IncidentProfileSignalTimelineResult Analyze(
        PreFaultIncident incident,
        IncidentEventChainStep step,
        MachineSignal signal,
        int maximumRenderedPoints = 3000)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(signal);

        if (step.Kind != IncidentTransitionKind.ByteChanged ||
            !step.DataIndex.HasValue)
        {
            throw new InvalidOperationException(
                "Profile-график доступен только для события изменения DATA[n].");
        }

        if (signal.CanId != step.Id ||
            signal.IsExtended != step.IsExtended)
        {
            throw new ArgumentException(
                "Machine Profile signal не соответствует CAN ID/Standard-Extended выбранного шага.",
                nameof(signal));
        }

        if (!MachineSignalDecoder.CoversDataByte(
                signal,
                step.DataIndex.Value))
        {
            throw new ArgumentException(
                $"Поле Machine Profile не охватывает изменившийся DATA[{step.DataIndex.Value}].",
                nameof(signal));
        }

        if (maximumRenderedPoints < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRenderedPoints),
                "Для графика требуется минимум 4 отображаемые точки.");
        }

        if (incident.Markers.Count == 0)
            throw new InvalidOperationException("Incident не содержит marker.");

        var marker = incident.Markers
            .OrderBy(item => item.Timestamp)
            .First()
            .Timestamp;
        var requiredDataLength =
            MachineSignalDecoder.RequiredDataLength(signal);

        var matchingFrames = incident.Frames
            .Where(frame => IsMatchingFrame(
                frame,
                signal.CanId,
                signal.IsExtended))
            .OrderBy(frame => frame.Timestamp)
            .ToArray();

        var shortFrameCount = matchingFrames.Count(
            frame => frame.Data.Length < requiredDataLength);

        var source = matchingFrames
            .Where(frame => frame.Data.Length >= requiredDataLength)
            .Select(frame =>
            {
                var decoded =
                    MachineSignalDecoder.Decode(signal, frame.Data);
                return new IncidentProfileSignalTimelinePoint(
                    (frame.Timestamp - marker).TotalMilliseconds,
                    decoded.EngineeringValue,
                    decoded.RawUnsigned,
                    decoded.RawSigned);
            })
            .ToArray();

        if (source.Length == 0)
        {
            throw new InvalidOperationException(
                $"В incident нет подходящих Rx кадров ID 0x{signal.CanId:X} " +
                $"с минимум {requiredDataLength} DATA-байт для сигнала «{signal.Name}».");
        }

        var warnings = new List<string>();

        if (shortFrameCount > 0)
        {
            warnings.Add(
                $"{shortFrameCount:N0} совпадающих кадров пропущено из-за короткого DLC; " +
                $"для поля требуется минимум {requiredDataLength} DATA-байт.");
        }

        var rendered = DownsamplePreservingExtremes(
            source,
            maximumRenderedPoints);
        if (rendered.Count < source.Length)
        {
            warnings.Add(
                $"Для визуализации {source.Length:N0} точек сокращены до {rendered.Count:N0}; " +
                "первый/последний отсчёт и локальные engineering min/max каждого временного блока сохранены. " +
                "Исходные CAN-кадры incident не изменены.");
        }

        if (source[0].RelativeMilliseconds > 0)
            warnings.Add("Для выбранного сигнала нет данных до marker.");
        if (source[^1].RelativeMilliseconds < 0)
            warnings.Add("Для выбранного сигнала нет данных после marker.");

        return new IncidentProfileSignalTimelineResult(
            marker,
            signal,
            step.ReactionMilliseconds,
            matchingFrames.Length,
            shortFrameCount,
            source.Length,
            rendered,
            source.Min(point => point.EngineeringValue),
            source.Max(point => point.EngineeringValue),
            source[0].RelativeMilliseconds,
            source[^1].RelativeMilliseconds,
            warnings);
    }

    private static bool IsMatchingFrame(
        CanFrame frame,
        uint id,
        bool isExtended)
    {
        frame.Validate();
        return frame.Protocol == BusProtocol.ClassicalCan &&
               frame.Direction == CanDirection.Rx &&
               !frame.IsRemote &&
               !frame.IsError &&
               frame.Id == id &&
               frame.IsExtended == isExtended;
    }

    private static IReadOnlyList<IncidentProfileSignalTimelinePoint>
        DownsamplePreservingExtremes(
            IReadOnlyList<IncidentProfileSignalTimelinePoint> source,
            int maximumRenderedPoints)
    {
        if (source.Count <= maximumRenderedPoints)
            return source.ToArray();

        var interiorBudget = maximumRenderedPoints - 2;
        var bucketCount = Math.Max(1, interiorBudget / 2);
        var selected = new SortedSet<int> { 0, source.Count - 1 };

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = 1 + (int)Math.Floor(
                bucket * (source.Count - 2) /
                (double)bucketCount);
            var endExclusive = 1 + (int)Math.Floor(
                (bucket + 1) * (source.Count - 2) /
                (double)bucketCount);
            endExclusive = Math.Max(
                start + 1,
                Math.Min(source.Count - 1, endExclusive));

            var minIndex = start;
            var maxIndex = start;
            for (var index = start + 1;
                 index < endExclusive;
                 index++)
            {
                if (source[index].EngineeringValue <
                    source[minIndex].EngineeringValue)
                {
                    minIndex = index;
                }

                if (source[index].EngineeringValue >
                    source[maxIndex].EngineeringValue)
                {
                    maxIndex = index;
                }
            }

            selected.Add(minIndex);
            selected.Add(maxIndex);
        }

        if (selected.Count > maximumRenderedPoints)
        {
            var ordered = selected.ToArray();
            selected.Clear();
            for (var index = 0;
                 index < maximumRenderedPoints;
                 index++)
            {
                var position =
                    index * (ordered.Length - 1) /
                    (double)(maximumRenderedPoints - 1);
                selected.Add(
                    ordered[(int)Math.Round(position)]);
            }
        }

        return selected
            .OrderBy(index => index)
            .Select(index => source[index])
            .ToArray();
    }
}
