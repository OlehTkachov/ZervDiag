using CraneCAN.Core.Live;
using CraneCAN.Core.Models;

namespace CraneCAN.Core.Analysis;

public sealed record IncidentSignalTimelinePoint(
    double RelativeMilliseconds,
    byte RawValue);

public sealed record IncidentSignalTimelineResult(
    DateTimeOffset MarkerTime,
    uint Id,
    bool IsExtended,
    int DataIndex,
    double TransitionMilliseconds,
    int SourceFrameCount,
    IReadOnlyList<IncidentSignalTimelinePoint> RenderedPoints,
    byte MinimumRawValue,
    byte MaximumRawValue,
    double StartMilliseconds,
    double EndMilliseconds,
    IReadOnlyList<string> Warnings);

public static class IncidentSignalTimelineAnalyzer
{
    public static IncidentSignalTimelineResult Analyze(
        PreFaultIncident incident,
        IncidentEventChainStep step,
        int maximumRenderedPoints = 3000)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(step);

        if (step.Kind != IncidentTransitionKind.ByteChanged || !step.DataIndex.HasValue)
            throw new InvalidOperationException(
                "График DATA доступен только для события изменения DATA[n].");
        if (maximumRenderedPoints < 4)
            throw new ArgumentOutOfRangeException(
                nameof(maximumRenderedPoints),
                "Для графика требуется минимум 4 отображаемые точки.");
        if (incident.Markers.Count == 0)
            throw new InvalidOperationException("Incident не содержит marker.");

        var marker = incident.Markers
            .OrderBy(item => item.Timestamp)
            .First()
            .Timestamp;
        var dataIndex = step.DataIndex.Value;

        var source = incident.Frames
            .Where(frame => IsUsable(frame, step.Id, step.IsExtended, dataIndex))
            .OrderBy(frame => frame.Timestamp)
            .Select(frame => new IncidentSignalTimelinePoint(
                (frame.Timestamp - marker).TotalMilliseconds,
                frame.Data[dataIndex]))
            .ToArray();

        if (source.Length == 0)
            throw new InvalidOperationException(
                $"В incident нет подходящих Rx кадров ID 0x{step.Id:X} с DATA[{dataIndex}].");

        var warnings = new List<string>();
        var rendered = DownsamplePreservingExtremes(source, maximumRenderedPoints);
        if (rendered.Count < source.Length)
        {
            warnings.Add(
                $"Для визуализации {source.Length:N0} точек сокращены до {rendered.Count:N0}; " +
                "первый/последний отсчёт и локальные min/max каждого временного блока сохранены. " +
                "Исходные CAN-кадры incident не изменены.");
        }

        if (source[0].RelativeMilliseconds > 0)
            warnings.Add("Для выбранного ID нет данных до marker.");
        if (source[^1].RelativeMilliseconds < 0)
            warnings.Add("Для выбранного ID нет данных после marker.");

        return new IncidentSignalTimelineResult(
            marker,
            step.Id,
            step.IsExtended,
            dataIndex,
            step.ReactionMilliseconds,
            source.Length,
            rendered,
            source.Min(point => point.RawValue),
            source.Max(point => point.RawValue),
            source[0].RelativeMilliseconds,
            source[^1].RelativeMilliseconds,
            warnings);
    }

    private static bool IsUsable(
        CanFrame frame,
        uint id,
        bool isExtended,
        int dataIndex)
    {
        frame.Validate();
        return frame.Protocol == BusProtocol.ClassicalCan &&
               frame.Direction == CanDirection.Rx &&
               !frame.IsRemote &&
               !frame.IsError &&
               frame.Id == id &&
               frame.IsExtended == isExtended &&
               frame.Data.Length > dataIndex;
    }

    private static IReadOnlyList<IncidentSignalTimelinePoint> DownsamplePreservingExtremes(
        IReadOnlyList<IncidentSignalTimelinePoint> source,
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
                bucket * (source.Count - 2) / (double)bucketCount);
            var endExclusive = 1 + (int)Math.Floor(
                (bucket + 1) * (source.Count - 2) / (double)bucketCount);
            endExclusive = Math.Max(start + 1, Math.Min(source.Count - 1, endExclusive));

            var minIndex = start;
            var maxIndex = start;
            for (var index = start + 1; index < endExclusive; index++)
            {
                if (source[index].RawValue < source[minIndex].RawValue)
                    minIndex = index;
                if (source[index].RawValue > source[maxIndex].RawValue)
                    maxIndex = index;
            }

            selected.Add(minIndex);
            selected.Add(maxIndex);
        }

        if (selected.Count > maximumRenderedPoints)
        {
            var ordered = selected.ToArray();
            selected.Clear();
            for (var index = 0; index < maximumRenderedPoints; index++)
            {
                var position = index * (ordered.Length - 1) /
                               (double)(maximumRenderedPoints - 1);
                selected.Add(ordered[(int)Math.Round(position)]);
            }
        }

        return selected
            .OrderBy(index => index)
            .Select(index => source[index])
            .ToArray();
    }
}
