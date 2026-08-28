namespace CraneCAN.Core.Analysis;

public sealed record FrameStatistics(
    string Id,
    long Count,
    int Dlc,
    double? AveragePeriodMilliseconds,
    double? MinimumPeriodMilliseconds,
    double? MaximumPeriodMilliseconds,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
