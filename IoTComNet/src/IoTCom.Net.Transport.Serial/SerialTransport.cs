using System.IO.Ports;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Transport.Serial;

/// <summary>Serial line settings.</summary>
public sealed record SerialSettings
{
    /// <summary>Port name, e.g. <c>COM3</c> or <c>/dev/ttyUSB0</c>.</summary>
    public required string PortName { get; init; }
    /// <summary>Baud rate (default 9600).</summary>
    public int BaudRate { get; init; } = 9600;
    /// <summary>Parity (Modbus RTU default is Even).</summary>
    public Parity Parity { get; init; } = Parity.None;
    /// <summary>Data bits.</summary>
    public int DataBits { get; init; } = 8;
    /// <summary>Stop bits.</summary>
    public StopBits StopBits { get; init; } = StopBits.One;
    /// <summary>Flow control.</summary>
    public Handshake Handshake { get; init; } = Handshake.None;
    /// <summary>Assert DTR (many USB adapters and modems need it).</summary>
    public bool DtrEnable { get; init; } = true;
    /// <summary>Assert RTS. For RS-485 prefer an adapter with automatic direction control.</summary>
    public bool RtsEnable { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{PortName}@{BaudRate} {DataBits}{Parity.ToString()[0]}{(StopBits == StopBits.Two ? 2 : 1)}";
}

/// <summary>
/// Serial transport over <see cref="SerialPort.BaseStream"/>. RS-485 direction control is expected from the
/// adapter (auto-direction); software RTS toggling is not timing-accurate on desktop OSes.
/// </summary>
public sealed class SerialTransport(SerialSettings settings) : StreamTransport
{
    private SerialPort? _port;

    /// <summary>Settings.</summary>
    public SerialSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <inheritdoc />
    public override TransportInfo Info => new(TransportKind.Serial, Settings.ToString(), Settings.PortName);

    /// <inheritdoc />
    protected override int MinimumReadSize => 512;

    /// <summary>Lists the serial ports present on this machine.</summary>
    public static string[] GetPortNames() => SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <inheritdoc />
    protected override ValueTask<Stream> OpenStreamAsync(CancellationToken ct)
    {
        var port = new SerialPort(Settings.PortName, Settings.BaudRate, Settings.Parity, Settings.DataBits, Settings.StopBits)
        {
            Handshake = Settings.Handshake,
            DtrEnable = Settings.DtrEnable,
            RtsEnable = Settings.RtsEnable,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
        };
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            var hint = OperatingSystem.IsLinux() ? " On Linux add your user to the 'dialout' group." : string.Empty;
            throw new TransportException($"Cannot open serial port {Settings.PortName}: {ex.Message}.{hint}", ex);
        }
        port.DiscardInBuffer();
        _port = port;
        return ValueTask.FromResult(port.BaseStream);
    }

    /// <inheritdoc />
    protected override ValueTask CloseCoreAsync()
    {
        _port?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary><c>UseSerial</c> builder extensions for every IoTCom.Net endpoint.</summary>
public static class SerialBuilderExtensions
{
    /// <summary>Connects through a serial port.</summary>
    public static T UseSerial<T>(this T builder, string portName, int baudRate = 9600, Parity parity = Parity.None, StopBits stopBits = StopBits.One, int dataBits = 8)
        where T : ITransportBuilder<T>
        => builder.UseSerial(new SerialSettings { PortName = portName, BaudRate = baudRate, Parity = parity, StopBits = stopBits, DataBits = dataBits });

    /// <summary>Connects through a serial port with full settings.</summary>
    public static T UseSerial<T>(this T builder, SerialSettings settings) where T : ITransportBuilder<T>
        => builder.UseTransport(() => new SerialTransport(settings));

    /// <summary>Serves (slave/device side) on a serial port.</summary>
    public static T ServeSerial<T>(this T builder, string portName, int baudRate = 9600, Parity parity = Parity.None, StopBits stopBits = StopBits.One)
        where T : IListenerBuilder<T>
        => builder.UseTransport(() => new SerialTransport(new SerialSettings { PortName = portName, BaudRate = baudRate, Parity = parity, StopBits = stopBits }));
}
