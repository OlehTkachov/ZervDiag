namespace CraneCAN.Core.Storage;

public sealed record PcanTrcScanProgress(double Percent, long FrameCount);

public sealed record PcanTrcScanResult(DateTimeOffset? FirstTimestamp, DateTimeOffset? LastTimestamp,
    long FrameCount, long ReceiveFrameCount, bool Ordered, int TotalLines,
    int RemoteFramesSkipped, int ErrorFramesSkipped, int UnknownOrMalformedLines);
