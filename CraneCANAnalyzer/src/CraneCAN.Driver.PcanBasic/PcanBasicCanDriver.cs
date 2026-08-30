using System.Runtime.CompilerServices;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Models;

namespace CraneCAN.Driver.PcanBasic;

/// <summary>Receive-only Classical CAN driver. The assembly exposes no transmit API.</summary>
public sealed class PcanBasicCanDriver : ICanDriver, ICanDriverDiagnostics
{
    private readonly object _sync = new();
    private ushort _handle;
    private int _channelIndex;
    private bool _open;
    private bool _listenOnly;
    private long _received;
    private long _lost;
    private long _errors;
    private string _message = "DISCONNECTED";

    public string Id => "peak-pcan-basic";
    public string DisplayName => "PEAK PCAN-USB (PCAN-Basic, LISTEN ONLY)";
    public BusProtocol Protocol => BusProtocol.ClassicalCan;
    public bool SupportsListenOnly => true;
    public bool IsOpen { get { lock (_sync) return _open; } }

    public Task<IReadOnlyList<CanChannelDescriptor>> DiscoverChannelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        try
        {
            IReadOnlyList<CanChannelDescriptor> channels = PcanBasicNative.UsbHandles
                .Select((handle, index) => (handle, index, status: PcanBasicNative.GetValue(
                    handle, PcanParameter.ChannelCondition, out var condition, sizeof(uint)), condition))
                .Where(item => item.status == PcanStatus.Ok && item.condition != 0)
                .Select(item => new CanChannelDescriptor(FormatChannelId(item.handle),
                    $"PCAN-USB Channel {item.index + 1} — {DescribeCondition(item.condition)}"))
                .ToArray();
            return Task.FromResult(channels);
        }
        catch (Exception ex) when (NativeFailure(ex)) { throw NativeLoadError(ex); }
    }

    public Task OpenAsync(CanChannelSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); EnsureWindows();
        if (!settings.ListenOnly) throw new InvalidOperationException("PCAN Live Guided запрещено открывать без LISTEN ONLY.");
        if (!TryParseChannelId(settings.ChannelId, out var handle)) throw new ArgumentException("Некорректный PCAN-USB канал.");
        if (!TryMapBitrate(settings.Bitrate, out var bitrate)) throw new NotSupportedException($"Bitrate {settings.Bitrate} не поддерживается.");
        lock (_sync)
        {
            if (_open) throw new InvalidOperationException("PCAN-канал уже открыт.");
            var initialized = false;
            try
            {
                var on = PcanBasicNative.ParameterOn;
                Ensure(PcanBasicNative.SetValue(handle, PcanParameter.ListenOnly, ref on, sizeof(uint)),
                    "PCAN не принял предварительный LISTEN ONLY");
                Ensure(PcanBasicNative.Initialize(handle, bitrate, 0, 0, 0), "Не удалось открыть PCAN");
                initialized = true;
                var status = PcanBasicNative.GetValue(handle, PcanParameter.ListenOnly, out var confirmed, sizeof(uint));
                if (status != PcanStatus.Ok || confirmed != PcanBasicNative.ParameterOn)
                    throw new InvalidOperationException("Аппаратный LISTEN ONLY не подтверждён. Канал закрыт.");
                Configure(handle, PcanParameter.ReceiveStatus, true);
                Configure(handle, PcanParameter.AllowStatusFrames, true);
                Configure(handle, PcanParameter.AllowRemoteFrames, false);
                Configure(handle, PcanParameter.AllowEchoFrames, false);
                Configure(handle, PcanParameter.AllowErrorFrames, settings.IncludeErrorFrames);
                _handle = handle; _channelIndex = Array.IndexOf(PcanBasicNative.UsbHandles, handle);
                _received = _lost = _errors = 0; _listenOnly = true; _open = true;
                _message = "CONNECTED — LISTEN ONLY";
            }
            catch (Exception ex)
            {
                if (initialized) PcanBasicNative.Uninitialize(handle);
                if (NativeFailure(ex)) throw NativeLoadError(ex);
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CanFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ushort handle; int channel;
        lock (_sync) { if (!_open) throw new InvalidOperationException("PCAN не открыт."); handle = _handle; channel = _channelIndex; }
        ulong? firstDevice = null; var firstWall = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested && IsOpen)
        {
            PcanStatus status; PcanMessage message; PcanTimestamp timestamp;
            try { status = PcanBasicNative.Read(handle, out message, out timestamp); }
            catch (Exception ex) when (NativeFailure(ex)) { throw NativeLoadError(ex); }
            if ((status & PcanStatus.ReceiveQueueEmpty) != 0)
            {
                var other = status & ~PcanStatus.ReceiveQueueEmpty;
                if (other != PcanStatus.Ok) RecordStatus(other);
                await Task.Delay(2, cancellationToken).ConfigureAwait(false); continue;
            }
            if (status != PcanStatus.Ok)
            {
                RecordStatus(status);
                if (Fatal(status)) throw new IOException($"PCAN перестал принимать: {PcanBasicNative.Describe(status)}");
                continue;
            }
            if ((message.Type & (PcanMessageType.ErrorFrame | PcanMessageType.Status)) != 0) { Interlocked.Increment(ref _errors); continue; }
            if ((message.Type & (PcanMessageType.Remote | PcanMessageType.Fd | PcanMessageType.Echo)) != 0 || message.Length > 8) continue;
            firstDevice ??= timestamp.TotalMicroseconds;
            var relative = timestamp.TotalMicroseconds >= firstDevice.Value ? timestamp.TotalMicroseconds - firstDevice.Value : 0;
            var frame = new CanFrame { Timestamp = firstWall.AddTicks(checked((long)relative * 10)), Channel = channel,
                Id = message.Id, IsExtended = (message.Type & PcanMessageType.Extended) != 0,
                Data = (message.Data ?? []).Take(message.Length).ToArray(), Protocol = BusProtocol.ClassicalCan,
                Direction = CanDirection.Rx };
            frame.Validate(); Interlocked.Increment(ref _received); yield return frame;
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ushort handle;
        lock (_sync) { if (!_open) return Task.CompletedTask; handle = _handle; _open = false; _listenOnly = false; _message = "DISCONNECTED"; }
        try { Ensure(PcanBasicNative.Uninitialize(handle), "Не удалось закрыть PCAN"); }
        catch (Exception ex) when (NativeFailure(ex)) { throw NativeLoadError(ex); }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
    public CanDriverStatus GetStatus()
    {
        lock (_sync)
        {
            if (_open) { var status = PcanBasicNative.GetStatus(_handle); if (status != PcanStatus.Ok) RecordStatus(status); }
            return new CanDriverStatus(_open, _listenOnly, Interlocked.Read(ref _received),
                Interlocked.Read(ref _lost), Interlocked.Read(ref _errors), _message);
        }
    }

    public static string FormatChannelId(ushort handle) => $"pcan-usb:{handle:X4}";
    public static bool TryParseChannelId(string? value, out ushort handle)
    {
        handle = 0; return value is not null && value.StartsWith("pcan-usb:", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(value[9..], System.Globalization.NumberStyles.HexNumber, null, out handle) && PcanBasicNative.UsbHandles.Contains(handle);
    }
    public static bool TryMapBitrate(int bitrate, out ushort code)
    {
        code = bitrate switch { 1_000_000 => 0x0014, 800_000 => 0x0016, 500_000 => 0x001C,
            250_000 => 0x011C, 125_000 => 0x031C, 100_000 => 0x432F, 83_333 => 0x852B,
            50_000 => 0x472F, 20_000 => 0x532F, 10_000 => 0x672F, 5_000 => 0x7F7F, _ => 0 };
        return code != 0;
    }
    private static void Configure(ushort handle, PcanParameter parameter, bool enabled)
    {
        var value = enabled ? PcanBasicNative.ParameterOn : PcanBasicNative.ParameterOff;
        var status = PcanBasicNative.SetValue(handle, parameter, ref value, sizeof(uint));
        if (status != PcanStatus.Ok) Ensure(status, $"Не удалось настроить {parameter}");
    }
    private static string DescribeCondition(uint condition) => condition switch
    {
        1 => "доступен",
        2 => "занят другим клиентом",
        3 => "PCAN-View активен",
        _ => $"состояние {condition}"
    };
    private void RecordStatus(PcanStatus status)
    {
        if ((status & (PcanStatus.Overrun | PcanStatus.ReceiveQueueOverrun)) != 0) Interlocked.Increment(ref _lost);
        if ((status & PcanStatus.AnyBusError) != 0) Interlocked.Increment(ref _errors);
        _message = PcanBasicNative.Describe(status);
    }
    private static bool Fatal(PcanStatus status) => (status & (PcanStatus.BusOff | PcanStatus.NoDriver |
        PcanStatus.IllegalHardware | PcanStatus.Initialize | PcanStatus.IllegalOperation)) != 0;
    private static void Ensure(PcanStatus status, string operation)
    { if (status != PcanStatus.Ok) throw new InvalidOperationException($"{operation}: {PcanBasicNative.Describe(status)}"); }
    private static void EnsureWindows()
    { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PCAN-Basic доступен только в Windows. Используйте TRC Replay."); }
    private static bool NativeFailure(Exception ex) => ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;
    private static InvalidOperationException NativeLoadError(Exception ex) => new(
        "PCANBasic.dll не найдена или несовместима. Установите официальный PEAK Device Driver x64.", ex);
}
