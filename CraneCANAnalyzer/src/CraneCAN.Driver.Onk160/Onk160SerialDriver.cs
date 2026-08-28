using System.IO.Ports;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Models;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Driver.Onk160;

public sealed class Onk160SerialDriver : ICanDriver
{
    public const int RequiredBitrate = 38_400;

    private readonly Onk160PacketParser _parser = new();
    private SerialPort? _serialPort;
    private int _channelNumber;

    public string Id => "onk160-serial";
    public string DisplayName => "ОНК-160 UART (COM, 38400 8E1)";
    public BusProtocol Protocol => BusProtocol.Onk160Serial;
    public bool SupportsListenOnly => true;
    public bool IsOpen => _serialPort?.IsOpen == true;

    public Task<IReadOnlyList<CanChannelDescriptor>> DiscoverChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CanChannelDescriptor> channels = GetAvailablePortNames()
            .OrderBy(PortSortKey)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new CanChannelDescriptor(name, $"{name} — приём ОНК-160"))
            .ToArray();
        return Task.FromResult(channels);
    }

    public Task OpenAsync(
        CanChannelSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.ListenOnly)
        {
            throw new InvalidOperationException("Драйвер ОНК-160 разрешает только пассивный приём.");
        }

        if (settings.Bitrate != RequiredBitrate)
        {
            throw new NotSupportedException(
                $"ОНК-160С-02 требует {RequiredBitrate} бит/с, 8E1. Выбрано: {settings.Bitrate} бит/с.");
        }

        if (IsOpen)
        {
            throw new InvalidOperationException("COM-порт ОНК-160 уже открыт.");
        }

        var portName = NormalizePortName(settings.ChannelId);
        if (!IsWindowsComPortName(portName))
        {
            throw new ArgumentException(
                $"Некорректное имя COM-порта: «{settings.ChannelId}». Введите, например, COM3.",
                nameof(settings));
        }

        var port = new SerialPort(portName, RequiredBitrate, Parity.Even, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadBufferSize = 4096,
            ReadTimeout = 500,
            WriteTimeout = 500,
            ParityReplace = 0,
            ReceivedBytesThreshold = 1
        };

        try
        {
            port.Open();
            port.DiscardInBuffer();
            _parser.Reset();
            _channelNumber = PortSortKey(portName);
            _serialPort = port;
        }
        catch (UnauthorizedAccessException exception)
        {
            port.Dispose();
            throw new InvalidOperationException(
                $"Не удалось открыть {portName}: порт занят другой программой или доступ запрещён. " +
                "Закройте терминалы и другие диагностические программы, затем повторите попытку.",
                exception);
        }
        catch (IOException exception)
        {
            port.Dispose();
            throw new InvalidOperationException(
                $"Windows не смогла открыть {portName}. Проверьте номер порта в Диспетчере устройств " +
                "и наличие драйвера USB-UART (VCP/Virtual COM Port).",
                exception);
        }
        catch (Exception exception)
        {
            port.Dispose();
            throw new InvalidOperationException(
                $"Не удалось открыть {portName}. Проверьте номер порта и драйвер USB-UART.",
                exception);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CanFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var port = _serialPort ?? throw new InvalidOperationException("COM-порт ОНК-160 не открыт.");
        var buffer = new byte[512];

        while (!cancellationToken.IsCancellationRequested && IsOpen)
        {
            var shouldStop = false;
            var received = 0;
            try
            {
                received = await port.BaseStream
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !IsOpen)
            {
                shouldStop = true;
            }

            if (shouldStop)
            {
                yield break;
            }

            if (received == 0)
            {
                yield break;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var packets = _parser.Append(buffer.AsSpan(0, received), timestamp);
            foreach (var packet in packets)
            {
                yield return new CanFrame
                {
                    Timestamp = packet.Timestamp,
                    Channel = _channelNumber,
                    Id = packet.Header,
                    Data = packet.Bytes.Skip(1).ToArray(),
                    Protocol = BusProtocol.Onk160Serial,
                    Direction = CanDirection.Rx,
                    IsChecksumValid = packet.IsChecksumValid
                };
            }
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = _serialPort;
        _serialPort = null;
        _parser.Reset();
        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            finally
            {
                port.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private static int PortSortKey(string portName)
    {
        if (portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(portName.AsSpan(3), out var number))
        {
            return number;
        }

        return int.MaxValue;
    }

    private static IReadOnlyCollection<string> GetAvailablePortNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var name in SerialPort.GetPortNames())
            {
                AddPortName(names, name);
            }
        }
        catch
        {
            // The Windows registry fallback below can still provide the port list.
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
                if (key is not null)
                {
                    foreach (var valueName in key.GetValueNames())
                    {
                        if (key.GetValue(valueName) is string portName)
                        {
                            AddPortName(names, portName);
                        }
                    }
                }
            }
            catch
            {
                // Manual COM entry in the UI remains available if registry access fails.
            }
        }

        return names;
    }

    private static void AddPortName(ISet<string> names, string? portName)
    {
        var normalized = NormalizePortName(portName);
        if (IsWindowsComPortName(normalized))
        {
            names.Add(normalized);
        }
    }

    private static string NormalizePortName(string? portName) =>
        (portName ?? string.Empty).Trim().ToUpperInvariant();

    private static bool IsWindowsComPortName(string portName) =>
        portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(portName.AsSpan(3), out var number) &&
        number is >= 1 and <= 4096;
}
