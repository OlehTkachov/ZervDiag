using CraneCAN.Core.Drivers;
using CraneCAN.Core.Models;
using CraneCAN.Driver.PcanBasic;

namespace CraneCAN.App;

internal sealed record PcanPassiveProbeAttempt(
    int Bitrate,
    long Frames,
    long ErrorFrames,
    long LostFrames,
    string Status,
    string? Failure = null);

internal sealed record PcanPassiveProbeReport(
    int? DetectedBitrate,
    IReadOnlyList<PcanPassiveProbeAttempt> Attempts)
{
    public PcanPassiveProbeAttempt? BestAttempt => Attempts
        .Where(item => item.Frames > 0)
        .OrderByDescending(item => item.Frames)
        .ThenBy(item => item.ErrorFrames)
        .FirstOrDefault();

    public string Describe()
    {
        if (BestAttempt is { } best)
            return $"{best.Bitrate:N0} bit/s: принято {best.Frames:N0} кадров, ошибок {best.ErrorFrames:N0}.";

        var opened = Attempts.Where(item => item.Failure is null).ToArray();
        if (opened.Length > 0)
        {
            var errors = opened.Sum(item => item.ErrorFrames);
            return $"Кадров не получено на {opened.Length} проверенных скоростях; ошибок CAN {errors:N0}.";
        }

        var failure = Attempts.Select(item => item.Failure).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        return failure ?? "PCAN-канал не удалось проверить.";
    }
}

internal static class PcanLiveProbe
{
    // Common Classical CAN bit rates supported by the PCAN driver. The usual crane/mobile-machine
    // rates are checked first so a healthy bus is detected quickly.
    public static IReadOnlyList<int> CommonBitrates { get; } =
    [
        250_000, 125_000, 500_000, 1_000_000,
        800_000, 100_000, 83_333, 50_000,
        20_000, 10_000, 5_000
    ];

    public static async Task<PcanPassiveProbeReport> ProbeAsync(
        string channelId,
        IEnumerable<int>? bitrates = null,
        TimeSpan? dwell = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("PCAN channel id is required.", nameof(channelId));

        var duration = dwell ?? TimeSpan.FromMilliseconds(350);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(dwell));

        var attempts = new List<PcanPassiveProbeAttempt>();
        foreach (var bitrate in (bitrates ?? CommonBitrates).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PcanBasicCanDriver.TryMapBitrate(bitrate, out _))
                continue;

            await using var driver = new PcanBasicCanDriver();
            try
            {
                await driver.OpenAsync(
                    new CanChannelSettings(channelId, bitrate, ListenOnly: true, IncludeErrorFrames: true),
                    cancellationToken).ConfigureAwait(false);

                long received = 0;
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(duration);
                try
                {
                    await foreach (var _ in driver.ReadFramesAsync(attemptCts.Token).ConfigureAwait(false))
                    {
                        received++;
                        // A handful of real data frames is enough to establish the bit rate.
                        if (received >= 16)
                            break;
                    }
                }
                catch (OperationCanceledException) when (
                    attemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
                var status = driver.GetStatus();
                attempts.Add(new PcanPassiveProbeAttempt(
                    bitrate,
                    Math.Max(received, status.ReceivedFrames),
                    status.ErrorFrames,
                    status.LostFrames,
                    status.Message));

                if (received > 0 || status.ReceivedFrames > 0)
                    return new PcanPassiveProbeReport(bitrate, attempts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                attempts.Add(new PcanPassiveProbeAttempt(
                    bitrate, 0, 0, 0, "OPEN FAILED", exception.Message));
            }
        }

        return new PcanPassiveProbeReport(null, attempts);
    }
}
