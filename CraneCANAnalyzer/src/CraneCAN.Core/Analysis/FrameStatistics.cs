using CraneCAN.Core.Models;

namespace CraneCAN.Core.Analysis;

public sealed record FrameStatistics(
    BusProtocol Protocol,
    uint Id,
    bool IsExtended,
    long Count,
    string DlcValues,
    double? AveragePeriodMilliseconds,
    double? MinimumPeriodMilliseconds,
    double? MaximumPeriodMilliseconds,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    public string IdText => Protocol == BusProtocol.ClassicalCan
        ? IsExtended ? Id.ToString("X8") : Id.ToString("X3")
        : Id.ToString("X2");

    public string FormatText => Protocol == BusProtocol.ClassicalCan
        ? IsExtended ? "Extended" : "Standard"
        : "ОНК UART";

    public double? FrequencyHertz => AveragePeriodMilliseconds is > 0
        ? 1000.0 / AveragePeriodMilliseconds.Value
        : null;
}
