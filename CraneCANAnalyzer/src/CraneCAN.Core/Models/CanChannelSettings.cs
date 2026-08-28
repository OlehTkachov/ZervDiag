namespace CraneCAN.Core.Models;

public sealed record CanChannelSettings(
    string ChannelId,
    int Bitrate,
    bool ListenOnly = true,
    bool IncludeErrorFrames = true);
