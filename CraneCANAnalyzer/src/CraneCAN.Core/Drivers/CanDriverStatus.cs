namespace CraneCAN.Core.Drivers;

public sealed record CanDriverStatus(
    bool Connected,
    bool ListenOnlyConfirmed,
    long ReceivedFrames,
    long LostFrames,
    long ErrorFrames,
    string Message);

public interface ICanDriverDiagnostics
{
    CanDriverStatus GetStatus();
}
