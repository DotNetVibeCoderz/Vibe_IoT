using System.Buffers.Binary;
using IoTCom.Net.Transport.Usb;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Transport.Can.Adapters;

/// <summary>Bit-timing limits reported by a gs_usb device (GS_USB_BREQ_BT_CONST).</summary>
/// <param name="Features">Feature bits (GS_CAN_FEATURE_*).</param>
/// <param name="ClockHz">CAN controller clock.</param>
/// <param name="Tseg1Min">Minimum TSEG1.</param>
/// <param name="Tseg1Max">Maximum TSEG1.</param>
/// <param name="Tseg2Min">Minimum TSEG2.</param>
/// <param name="Tseg2Max">Maximum TSEG2.</param>
/// <param name="SjwMax">Maximum SJW.</param>
/// <param name="BrpMin">Minimum prescaler.</param>
/// <param name="BrpMax">Maximum prescaler.</param>
/// <param name="BrpInc">Prescaler step.</param>
public sealed record GsUsbBitTimingConstants(uint Features, uint ClockHz, uint Tseg1Min, uint Tseg1Max, uint Tseg2Min, uint Tseg2Max, uint SjwMax, uint BrpMin, uint BrpMax, uint BrpInc)
{
    /// <summary>The device supports CAN FD (GS_CAN_FEATURE_FD).</summary>
    public bool SupportsFd => (Features & GsUsbCodec.FeatureFd) != 0;

    /// <summary>The device timestamps frames (GS_CAN_FEATURE_HW_TIMESTAMP).</summary>
    public bool SupportsTimestamps => (Features & GsUsbCodec.FeatureHwTimestamp) != 0;

    /// <summary>Parses the 40-byte little-endian structure.</summary>
    public static GsUsbBitTimingConstants Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 40) throw new FormatException($"gs_usb BT_CONST is 40 bytes, got {b.Length}.");
        var v = new uint[10];
        for (var i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(i * 4)..]);
        return new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);
    }

    /// <summary>Encodes the 40-byte structure.</summary>
    public byte[] Encode()
    {
        var b = new byte[40];
        uint[] v = [Features, ClockHz, Tseg1Min, Tseg1Max, Tseg2Min, Tseg2Max, SjwMax, BrpMin, BrpMax, BrpInc];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }
}

/// <summary>Bit timing sent with GS_USB_BREQ_BITTIMING (TSEG1 = PropSeg + PhaseSeg1).</summary>
/// <param name="PropSeg">Propagation segment.</param>
/// <param name="PhaseSeg1">Phase segment 1.</param>
/// <param name="PhaseSeg2">Phase segment 2.</param>
/// <param name="Sjw">Synchronisation jump width.</param>
/// <param name="Brp">Prescaler.</param>
public sealed record GsUsbBitTiming(uint PropSeg, uint PhaseSeg1, uint PhaseSeg2, uint Sjw, uint Brp)
{
    /// <summary>Time quanta per bit.</summary>
    public uint Quanta => 1 + PropSeg + PhaseSeg1 + PhaseSeg2;

    /// <summary>Sample point (0..1).</summary>
    public double SamplePoint => (double)(1 + PropSeg + PhaseSeg1) / Quanta;

    /// <summary>Bit rate this timing produces at <paramref name="clockHz"/>.</summary>
    public double BitrateAt(uint clockHz) => (double)clockHz / (Brp * Quanta);

    /// <summary>
    /// Chooses the smallest prescaler giving an exact bit rate with 8–25 quanta and a sample point closest to
    /// <paramref name="samplePoint"/> (CiA recommends 87.5 %).
    /// </summary>
    public static GsUsbBitTiming Calculate(GsUsbBitTimingConstants c, int bitrate, double samplePoint = 0.875)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitrate);
        GsUsbBitTiming? best = null;
        var bestError = double.MaxValue;
        for (var brp = Math.Max(1, c.BrpMin); brp <= c.BrpMax; brp += Math.Max(1, c.BrpInc))
        {
            if (c.ClockHz % (brp * (uint)bitrate) != 0) continue;
            var tq = c.ClockHz / (brp * (uint)bitrate);
            if (tq is < 8 or > 25) continue;
            var tseg2 = Math.Clamp((uint)Math.Round(tq * (1 - samplePoint)), c.Tseg2Min, c.Tseg2Max);
            var tseg1 = tq - 1 - tseg2;
            if (tseg1 < c.Tseg1Min || tseg1 > c.Tseg1Max) continue;
            var t = new GsUsbBitTiming(1, tseg1 - 1, tseg2, Math.Min(c.SjwMax, Math.Min(4, tseg2)), brp);
            var error = Math.Abs(t.SamplePoint - samplePoint);
            if (error < bestError - 1e-9)
            {
                (best, bestError) = (t, error);
            }
        }

        return best ?? throw new ArgumentException($"{bitrate} bit/s cannot be produced exactly from a {c.ClockHz} Hz clock.", nameof(bitrate));
    }

    /// <summary>Encodes the 20-byte structure.</summary>
    public byte[] Encode()
    {
        var b = new byte[20];
        uint[] v = [PropSeg, PhaseSeg1, PhaseSeg2, Sjw, Brp];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    /// <summary>Parses the 20-byte structure.</summary>
    public static GsUsbBitTiming Parse(ReadOnlySpan<byte> b) =>
        b.Length < 20 ? throw new FormatException("gs_usb bit timing is 20 bytes.")
            : new(BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(b[12..]), BinaryPrimitives.ReadUInt32LittleEndian(b[16..]));
}

/// <summary>
/// The gs_usb host protocol (Linux <c>drivers/net/can/usb/gs_usb.c</c>; candleLight, CANable 2 with candleLight
/// firmware, CANtact Pro, Microchip/Geschwister Schneider adapters): vendor control requests and 20-byte (classic)
/// or 76-byte (CAN FD) little-endian host frames.
/// </summary>
public static class GsUsbCodec
{
    /// <summary>GS_USB_BREQ_HOST_FORMAT.</summary>
    public const byte RequestHostFormat = 0;
    /// <summary>GS_USB_BREQ_BITTIMING.</summary>
    public const byte RequestBitTiming = 1;
    /// <summary>GS_USB_BREQ_MODE.</summary>
    public const byte RequestMode = 2;
    /// <summary>GS_USB_BREQ_BT_CONST.</summary>
    public const byte RequestBitTimingConstants = 4;
    /// <summary>GS_USB_BREQ_DEVICE_CONFIG.</summary>
    public const byte RequestDeviceConfig = 5;
    /// <summary>GS_USB_BREQ_IDENTIFY (blink the LED).</summary>
    public const byte RequestIdentify = 7;
    /// <summary>GS_USB_BREQ_DATA_BITTIMING.</summary>
    public const byte RequestDataBitTiming = 10;

    /// <summary>Mode: reset (stop).</summary>
    public const uint ModeReset = 0;
    /// <summary>Mode: start.</summary>
    public const uint ModeStart = 1;
    /// <summary>GS_CAN_MODE_LISTEN_ONLY.</summary>
    public const uint FlagListenOnly = 1 << 0;
    /// <summary>GS_CAN_MODE_LOOP_BACK.</summary>
    public const uint FlagLoopBack = 1 << 1;
    /// <summary>GS_CAN_MODE_HW_TIMESTAMP.</summary>
    public const uint FlagHwTimestamp = 1 << 4;
    /// <summary>GS_CAN_MODE_FD.</summary>
    public const uint FlagFd = 1 << 8;

    /// <summary>GS_CAN_FEATURE_HW_TIMESTAMP.</summary>
    public const uint FeatureHwTimestamp = 1 << 4;
    /// <summary>GS_CAN_FEATURE_FD.</summary>
    public const uint FeatureFd = 1 << 8;

    /// <summary>echo_id of received frames.</summary>
    public const uint EchoIdRx = 0xFFFF_FFFF;

    /// <summary>Host frame flags.</summary>
    public const byte FrameOverflow = 1, FrameFd = 2, FrameBrs = 4, FrameEsi = 8;

    /// <summary>Value of GS_USB_BREQ_HOST_FORMAT (little-endian byte order).</summary>
    public static byte[] HostFormat => [0xEF, 0xBE, 0x00, 0x00];

    /// <summary>Mode structure (mode, flags).</summary>
    public static byte[] Mode(uint mode, uint flags)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, mode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), flags);
        return b;
    }

    /// <summary>Encodes a host frame: echo_id, can_id (SocketCAN flag bits), dlc, channel, flags, reserved, data.</summary>
    public static byte[] EncodeFrame(uint echoId, in CanFrame frame, byte channel = 0, bool hwTimestamp = false)
    {
        var dataLength = frame.IsFd ? 64 : 8;
        var b = new byte[12 + dataLength + (hwTimestamp ? 4 : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(b, echoId);
        var id = frame.Id | (frame.IsExtended ? 0x8000_0000u : 0) | (frame.IsRemote ? 0x4000_0000u : 0) | (frame.IsError ? 0x2000_0000u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), id);
        b[8] = (byte)frame.Dlc;
        b[9] = channel;
        b[10] = (byte)((frame.IsFd ? FrameFd : 0) | ((frame.Flags & CanFrameFlags.BitRateSwitch) != 0 ? FrameBrs : 0) | ((frame.Flags & CanFrameFlags.ErrorStateIndicator) != 0 ? FrameEsi : 0));
        frame.Data.Span.CopyTo(b.AsSpan(12));
        return b;
    }

    /// <summary>Decodes a host frame; returns the echo id (0xFFFFFFFF for received frames).</summary>
    public static (uint EchoId, CanFrame Frame, byte Channel, uint? TimestampMicros) DecodeFrame(ReadOnlySpan<byte> b, bool hwTimestamp = false)
    {
        if (b.Length < 20) throw new FormatException($"A gs_usb host frame has at least 20 bytes, got {b.Length}.");
        var echo = BinaryPrimitives.ReadUInt32LittleEndian(b);
        var rawId = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        var dlc = b[8];
        var flags = b[10];
        var fd = (flags & FrameFd) != 0;
        var cf = (rawId & 0x8000_0000) != 0 ? CanFrameFlags.Extended : CanFrameFlags.None;
        if ((rawId & 0x4000_0000) != 0) cf |= CanFrameFlags.Remote;
        if ((rawId & 0x2000_0000) != 0) cf |= CanFrameFlags.Error;
        if (fd) cf |= CanFrameFlags.Fd;
        if ((flags & FrameBrs) != 0) cf |= CanFrameFlags.BitRateSwitch;
        if ((flags & FrameEsi) != 0) cf |= CanFrameFlags.ErrorStateIndicator;
        var dataSpace = fd ? 64 : 8;
        if (b.Length < 12 + dataSpace) throw new FormatException($"gs_usb {(fd ? "FD" : "classic")} frame needs {12 + dataSpace} bytes.");
        var length = (cf & CanFrameFlags.Remote) != 0 ? 0 : Math.Min(CanDlc.ToLength(dlc, fd), dataSpace);
        var id = rawId & ((cf & CanFrameFlags.Extended) != 0 ? CanFrame.MaxExtendedId : CanFrame.MaxStandardId);
        var frame = new CanFrame(id, b.Slice(12, length).ToArray(), cf, (cf & CanFrameFlags.Remote) != 0 ? dlc : 0);
        uint? ts = hwTimestamp && b.Length >= 12 + dataSpace + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b[(12 + dataSpace)..]) : null;
        return (echo, frame, b[9], ts);
    }
}

/// <summary>
/// A gs_usb / candleLight CAN adapter as an <see cref="ICanBus"/> (<c>gsusb:</c>, <c>gsusb:1d50:606f</c>,
/// <c>gsusb:1d50:606f:SERIAL#1</c> for channel 1). Raw USB access needs WinUSB on Windows (candleLight ships with it)
/// and a udev rule on Linux, where the kernel gs_usb driver may be used through <c>socketcan:</c> instead.
/// </summary>
public sealed class GsUsbCanBus : CanBusBase
{
    /// <summary>OpenMoko vendor id used by candleLight firmware.</summary>
    public const string DefaultDeviceId = "1d50:606f";

    private const byte InEndpoint = 0x81, OutEndpoint = 0x02;
    private readonly IUsbBackend _backend;
    private readonly string _deviceId;
    private readonly byte _channel;
    private IUsbDeviceHandle? _usb;
    private CancellationTokenSource? _cts;
    private Task? _rx;
    private uint _echo;
    private bool _timestamps;

    /// <summary>Creates a bus on <paramref name="deviceId"/> (vvvv:pppp[:serial]) channel <paramref name="channel"/>.</summary>
    public GsUsbCanBus(string? deviceId = null, byte channel = 0, CanBusOptions? options = null, IUsbBackend? backend = null)
        : base("can-gsusb", $"gsusb:{deviceId ?? DefaultDeviceId}#{channel}", options ?? new CanBusOptions())
    {
        _backend = backend ?? NativeUsbBackend.Instance;
        _deviceId = string.IsNullOrEmpty(deviceId) ? DefaultDeviceId : deviceId;
        _channel = channel;
    }

    /// <summary>Bit-timing limits reported by the adapter (after connect).</summary>
    public GsUsbBitTimingConstants? Constants { get; private set; }

    /// <summary>Bit timing in use (after connect).</summary>
    public GsUsbBitTiming? Timing { get; private set; }

    /// <summary>Firmware and hardware versions (after connect).</summary>
    public (uint Software, uint Hardware, int Channels)? DeviceConfig { get; private set; }

    /// <inheritdoc />
    public override bool SupportsFd => Options.Fd && Constants?.SupportsFd == true;

    private static UsbSetup Out(byte request, byte channel) => new(0x41, request, channel, 0);

    /// <inheritdoc />
    protected override async ValueTask OpenCoreAsync(CancellationToken ct)
    {
        _usb = await Task.Run(() => _backend.Open(_deviceId), ct).ConfigureAwait(false);
        var timeout = TimeSpan.FromSeconds(1);
        try
        {
            _usb.Claim(0, detachKernelDriver: true);
            _usb.ControlOut(0, Out(GsUsbCodec.RequestHostFormat, 0), GsUsbCodec.HostFormat, timeout);
            var config = _usb.ControlIn(0, new UsbSetup(0xC1, GsUsbCodec.RequestDeviceConfig, 0, 0), 12, timeout);
            if (config.Length >= 12)
                DeviceConfig = (BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(config.AsSpan(8)), config[3] + 1);
            if (DeviceConfig is { } dc && _channel >= dc.Channels) throw new ArgumentException($"The adapter has {dc.Channels} channel(s); channel {_channel} does not exist.");
            Constants = GsUsbBitTimingConstants.Parse(_usb.ControlIn(0, new UsbSetup(0xC1, GsUsbCodec.RequestBitTimingConstants, _channel, 0), 40, timeout));
            if (Options.Fd && !Constants.SupportsFd) throw new NotSupportedException("This gs_usb adapter does not support CAN FD.");
            Timing = GsUsbBitTiming.Calculate(Constants, Options.Bitrate);
            _usb.ControlOut(0, Out(GsUsbCodec.RequestBitTiming, _channel), Timing.Encode(), timeout);
            if (Options.Fd) _usb.ControlOut(0, Out(GsUsbCodec.RequestDataBitTiming, _channel), GsUsbBitTiming.Calculate(Constants, Options.DataBitrate, 0.75).Encode(), timeout);
            _timestamps = Constants.SupportsTimestamps;
            var flags = (Options.ListenOnly ? GsUsbCodec.FlagListenOnly : 0) | (_timestamps ? GsUsbCodec.FlagHwTimestamp : 0) | (Options.Fd ? GsUsbCodec.FlagFd : 0);
            _usb.ControlOut(0, Out(GsUsbCodec.RequestMode, _channel), GsUsbCodec.Mode(GsUsbCodec.ModeStart, flags), timeout);
        }
        catch
        {
            _usb.Dispose();
            _usb = null;
            throw;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _rx = Task.Run(() => ReceiveLoop(token), CancellationToken.None);
        Logger.LogInformation("gs_usb {Device} channel {Channel}: {Bitrate} bit/s, brp {Brp}, {Quanta} tq, sample point {Sp:P1}",
            _deviceId, _channel, Options.Bitrate, Timing.Brp, Timing.Quanta, Timing.SamplePoint);
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[]? data;
            try
            {
                data = _usb!.Read(InEndpoint, interrupt: false, Options.Fd ? 80 : 24, TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex) when (ex is IoTComException or ObjectDisposedException)
            {
                if (!ct.IsCancellationRequested) Fault(ex);
                return;
            }

            if (data is null) continue;
            try
            {
                var (echo, frame, channel, _) = GsUsbCodec.DecodeFrame(data, _timestamps);
                if (echo == GsUsbCodec.EchoIdRx && channel == _channel) OnFrameReceived(frame);
            }
            catch (FormatException ex)
            {
                Logger.LogDebug(ex, "gs_usb: dropped a malformed host frame");
            }
        }
    }

    /// <inheritdoc />
    protected override ValueTask SendCoreAsync(CanFrame frame, CancellationToken ct)
    {
        var echo = Interlocked.Increment(ref _echo) % 64;   // echo ids are recycled; the firmware keeps a small pool
        var bytes = GsUsbCodec.EncodeFrame(echo, frame, _channel, _timestamps);
        return new ValueTask(Task.Run(() => _usb!.Write(OutEndpoint, interrupt: false, bytes, TimeSpan.FromSeconds(1)), ct));
    }

    /// <inheritdoc />
    protected override async ValueTask CloseCoreAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_rx is not null) await _rx.ConfigureAwait(false);
        try
        {
            _usb?.ControlOut(0, Out(GsUsbCodec.RequestMode, _channel), GsUsbCodec.Mode(GsUsbCodec.ModeReset, 0), TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex) when (ex is IoTComException or ArgumentException)
        {
        }

        _usb?.Dispose();
        _usb = null;
        _cts?.Dispose();
        _cts = null;
    }
}

/// <summary>
/// A simulated candleLight adapter for a <see cref="VirtualUsbBus"/>: it answers the gs_usb control requests (48 MHz
/// clock, one channel, no FD) and bridges its bulk endpoints to a <see cref="VirtualCanNetwork"/>, so gs_usb code can be
/// tested against virtual ECUs.
/// </summary>
public sealed class VirtualGsUsbDevice : VirtualUsbDevice, IAsyncDisposable
{
    private readonly VirtualCanBus _node;
    private bool _started;

    /// <summary>Creates the adapter on <paramref name="network"/>.</summary>
    public VirtualGsUsbDevice(VirtualCanNetwork network, string serial = "0042")
    {
        ArgumentNullException.ThrowIfNull(network);
        Info = new UsbDeviceInfo
        {
            Id = $"1d50:606f:{serial}", VendorId = 0x1D50, ProductId = 0x606F, Manufacturer = "bytewerk", Product = "candleLight USB to CAN adapter", Serial = serial,
            Bus = "virtual", Address = 3, Interfaces = [new UsbInterfaceInfo(0, 0xFF, 0xFF, 0xFF, null)],
        };
        _node = network.CreateNode();
        _node.FrameReceived += f =>
        {
            if (_started) Send(0x81, GsUsbCodec.EncodeFrame(GsUsbCodec.EchoIdRx, f));
        };
        _node.ConnectAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override UsbDeviceInfo Info { get; }

    /// <summary>Constants reported to the host.</summary>
    public GsUsbBitTimingConstants Constants { get; } = new(0, 48_000_000, 1, 16, 1, 8, 4, 1, 1024, 1);

    /// <summary>Last bit timing written by the host.</summary>
    public GsUsbBitTiming? Timing { get; private set; }

    /// <summary>Mode flags of the last start.</summary>
    public uint ModeFlags { get; private set; }

    /// <inheritdoc />
    protected override byte[]? ControlIn(UsbSetup setup, int length) => setup.Request switch
    {
        GsUsbCodec.RequestBitTimingConstants => Constants.Encode(),
        GsUsbCodec.RequestDeviceConfig => [0, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0],
        _ => null,
    };

    /// <inheritdoc />
    protected override bool ControlOut(UsbSetup setup, byte[] data)
    {
        switch (setup.Request)
        {
            case GsUsbCodec.RequestHostFormat:
                return data.AsSpan().SequenceEqual(GsUsbCodec.HostFormat);
            case GsUsbCodec.RequestBitTiming:
                Timing = GsUsbBitTiming.Parse(data);
                return true;
            case GsUsbCodec.RequestMode:
                _started = BinaryPrimitives.ReadUInt32LittleEndian(data) == GsUsbCodec.ModeStart;
                ModeFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                return true;
            case GsUsbCodec.RequestIdentify:
                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    protected override void Received(byte endpoint, byte[] data)
    {
        if (endpoint != 0x02 || !_started) return;
        var (echo, frame, _, _) = GsUsbCodec.DecodeFrame(data);
        if ((ModeFlags & GsUsbCodec.FlagListenOnly) == 0) _node.SendAsync(frame).AsTask().GetAwaiter().GetResult();
        Send(0x81, GsUsbCodec.EncodeFrame(echo, frame));   // TX echo: the host learns the frame left the adapter
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _node.DisposeAsync();
}
