using System.Runtime.CompilerServices;
using System.Text;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Transport.Usb;

/// <summary>USB device options.</summary>
public sealed class UsbDeviceOptions
{
    /// <summary>Backend (default: the native library).</summary>
    public IUsbBackend Backend { get; set; } = NativeUsbBackend.Instance;

    /// <summary>Device id <c>vvvv:pppp[:serial]</c>.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Interfaces to claim on connect (default: interface 0).</summary>
    public IList<byte> Interfaces { get; } = [0];

    /// <summary>Detach a kernel driver from the interfaces first (Linux).</summary>
    public bool DetachKernelDriver { get; set; }

    /// <summary>Refuse control OUT transfers and endpoint writes with <see cref="ReadOnlyModeException"/>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Default transfer timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Selects the device by vendor and product id.</summary>
    public UsbDeviceOptions UseDevice(ushort vendorId, ushort productId, string? serial = null)
    {
        DeviceId = $"{vendorId:x4}:{productId:x4}{(serial is null ? "" : ":" + serial)}";
        return this;
    }

    /// <summary>Uses a virtual bus.</summary>
    public UsbDeviceOptions UseVirtual(VirtualUsbBus bus)
    {
        Backend = bus;
        return this;
    }
}

/// <summary>
/// A raw USB device: control transfers on endpoint 0 and bulk/interrupt transfers on the claimed interfaces. Writes and
/// control OUT requests are refused in read-only mode. Every transfer is reported to the traffic tap.
/// </summary>
public sealed class UsbDevice : EndpointBase, IClientEndpoint
{
    private readonly UsbDeviceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IUsbDeviceHandle? _handle;

    private UsbDevice(UsbDeviceOptions options) : base("usb", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a device endpoint.</summary>
    public static UsbDevice Create(Action<UsbDeviceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new UsbDeviceOptions();
        configure(o);
        return new UsbDevice(o);
    }

    /// <summary>Lists the devices of a backend (default: native).</summary>
    public static IReadOnlyList<UsbDeviceInfo> List(IUsbBackend? backend = null) => (backend ?? NativeUsbBackend.Instance).List();

    /// <summary>The device's enumeration info (after connect).</summary>
    public UsbDeviceInfo? Info { get; private set; }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_handle is not null) return;
        SetState(EndpointState.Connecting);
        try
        {
            _handle = await Task.Run(() =>
            {
                var h = _options.Backend.Open(_options.DeviceId);
                try
                {
                    foreach (var i in _options.Interfaces) h.Claim(i, _options.DetachKernelDriver);
                }
                catch
                {
                    h.Dispose();
                    throw;
                }

                return h;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IoTComException or PlatformNotSupportedException)
        {
            SetState(EndpointState.Disconnected, ex);
            throw;
        }

        Info = _options.Backend.List().FirstOrDefault(d => d.Id.StartsWith(_options.DeviceId, StringComparison.OrdinalIgnoreCase));
        Tap(FrameDirection.Outbound, Encoding.UTF8.GetBytes($"open {_options.DeviceId}"), () => $"open {Info?.ToString() ?? _options.DeviceId}");
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        _handle?.Dispose();
        _handle = null;
        SetState(EndpointState.Disconnected);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private IUsbDeviceHandle Live => _handle ?? throw new InvalidOperationException("Connect first.");

    private async Task<T> Run<T>(Func<IUsbDeviceHandle, T> call, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var h = Live;
            return await Task.Run(() => call(h), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Control IN transfer (the direction bit is set for you).</summary>
    public async Task<byte[]> ControlInAsync(UsbSetup setup, int length, CancellationToken ct = default)
    {
        var data = await Run(h => h.ControlIn(0xFF, setup, length, _options.Timeout), ct).ConfigureAwait(false);
        Tap(FrameDirection.Inbound, data, () => $"control IN 0x{setup.RequestType | 0x80:x2}/0x{setup.Request:x2} → {data.Length} B");
        return data;
    }

    /// <summary>Control OUT transfer; refused in read-only mode.</summary>
    public async Task ControlOutAsync(UsbSetup setup, ReadOnlyMemory<byte> data = default, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        Tap(FrameDirection.Outbound, data.Span, () => $"control OUT 0x{setup.RequestType & 0x7F:x2}/0x{setup.Request:x2} value 0x{setup.Value:x4}");
        await Run(h =>
        {
            h.ControlOut(0xFF, setup, data.Span, _options.Timeout);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Writes to a bulk (or interrupt) OUT endpoint; refused in read-only mode.</summary>
    public async Task<int> WriteAsync(byte endpoint, ReadOnlyMemory<byte> data, bool interrupt = false, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        Tap(FrameDirection.Outbound, data.Span, () => $"{(interrupt ? "interrupt" : "bulk")} OUT 0x{endpoint:x2} {data.Length} B");
        return await Run(h => h.Write(endpoint, interrupt, data.Span, _options.Timeout), ct).ConfigureAwait(false);
    }

    /// <summary>Reads one transfer from a bulk (or interrupt) IN endpoint; null when <paramref name="timeout"/> passes without data.</summary>
    public async Task<byte[]?> ReadAsync(byte endpoint, int length = 512, TimeSpan? timeout = null, bool interrupt = false, CancellationToken ct = default)
    {
        var data = await Run(h => h.Read(endpoint, interrupt, length, timeout ?? _options.Timeout), ct).ConfigureAwait(false);
        if (data is not null) Tap(FrameDirection.Inbound, data, () => $"{(interrupt ? "interrupt" : "bulk")} IN 0x{endpoint:x2} {data.Length} B");
        return data;
    }

    /// <summary>Reads an IN endpoint continuously until cancelled.</summary>
    public async IAsyncEnumerable<byte[]> StreamAsync(byte endpoint, int length = 512, bool interrupt = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var data = await ReadAsync(endpoint, length, TimeSpan.FromMilliseconds(250), interrupt, ct).ConfigureAwait(false);
            if (data is not null) yield return data;
        }
    }
}

/// <summary>HID device options.</summary>
public sealed class HidDeviceOptions
{
    /// <summary>Backend (default: the native library).</summary>
    public IUsbBackend Backend { get; set; } = NativeUsbBackend.Instance;

    /// <summary>Platform path (from <see cref="HidDevice.List"/>); when empty the first collection matching vid/pid is used.</summary>
    public string? Path { get; set; }

    /// <summary>Vendor id filter.</summary>
    public ushort? VendorId { get; set; }

    /// <summary>Product id filter.</summary>
    public ushort? ProductId { get; set; }

    /// <summary>Usage page filter (e.g. 0xFF00 for a vendor collection).</summary>
    public ushort? UsagePage { get; set; }

    /// <summary>Refuse output and feature reports with <see cref="ReadOnlyModeException"/>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Selects a device by vendor/product id.</summary>
    public HidDeviceOptions UseDevice(ushort vendorId, ushort productId)
    {
        (VendorId, ProductId) = (vendorId, productId);
        return this;
    }

    /// <summary>Uses a virtual bus.</summary>
    public HidDeviceOptions UseVirtual(VirtualUsbBus bus)
    {
        Backend = bus;
        return this;
    }
}

/// <summary>A HID collection: input, output and feature reports. Output and feature writes are refused in read-only mode.</summary>
public sealed class HidDevice : EndpointBase, IClientEndpoint
{
    private readonly HidDeviceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IHidHandle? _handle;

    private HidDevice(HidDeviceOptions options) : base("hid", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a HID endpoint.</summary>
    public static HidDevice Create(Action<HidDeviceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new HidDeviceOptions();
        configure(o);
        return new HidDevice(o);
    }

    /// <summary>Lists HID collections (default backend: native).</summary>
    public static IReadOnlyList<HidDeviceInfo> List(IUsbBackend? backend = null) => (backend ?? NativeUsbBackend.Instance).ListHid();

    /// <summary>The opened collection.</summary>
    public HidDeviceInfo? Info { get; private set; }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_handle is not null) return;
        SetState(EndpointState.Connecting);
        try
        {
            (_handle, Info) = await Task.Run(() =>
            {
                var info = _options.Backend.ListHid().FirstOrDefault(d =>
                    (_options.Path is null || d.Path == _options.Path) && (_options.VendorId is null || d.VendorId == _options.VendorId)
                    && (_options.ProductId is null || d.ProductId == _options.ProductId) && (_options.UsagePage is null || d.UsagePage == _options.UsagePage))
                    ?? throw new DeviceException("No matching HID device is connected.");
                return (_options.Backend.OpenHid(info.Path), info);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IoTComException or PlatformNotSupportedException)
        {
            SetState(EndpointState.Disconnected, ex);
            throw;
        }

        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        _handle?.Dispose();
        _handle = null;
        SetState(EndpointState.Disconnected);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task<T> Run<T>(Func<IHidHandle, T> call, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var h = _handle ?? throw new InvalidOperationException("Connect first.");
            return await Task.Run(() => call(h), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes an output report (byte 0 = report id); refused in read-only mode.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        Tap(FrameDirection.Outbound, report.Span, () => $"output report {report.Length} B");
        await Run(h => h.Write(report.Span), ct).ConfigureAwait(false);
    }

    /// <summary>Reads one input report; null on timeout.</summary>
    public async Task<byte[]?> ReadAsync(int length = 64, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var r = await Run(h => h.Read(length, timeout ?? TimeSpan.FromSeconds(1)), ct).ConfigureAwait(false);
        if (r is not null) Tap(FrameDirection.Inbound, r, () => $"input report {r.Length} B");
        return r;
    }

    /// <summary>Yields input reports until cancelled.</summary>
    public async IAsyncEnumerable<byte[]> ReportsAsync(int length = 64, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var r = await ReadAsync(length, TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            if (r is not null) yield return r;
        }
    }

    /// <summary>Sends a feature report (byte 0 = report id); refused in read-only mode.</summary>
    public async Task SendFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException();
        Tap(FrameDirection.Outbound, report.Span, () => $"set feature {report.Length} B");
        await Run(h =>
        {
            h.SendFeature(report.Span);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a feature report.</summary>
    public async Task<byte[]> GetFeatureAsync(byte reportId, int length, CancellationToken ct = default)
    {
        var r = await Run(h => h.GetFeature(reportId, length), ct).ConfigureAwait(false);
        Tap(FrameDirection.Inbound, r, () => $"get feature {reportId} → {r.Length} B");
        return r;
    }
}

/// <summary>
/// A "USBRelayN" HID relay board (dcttech / V-USB 16c0:05df, sold everywhere as 1/2/4/8-channel USB relay modules).
/// </summary>
public sealed class HidRelayBoard(HidDevice device)
{
    /// <summary>V-USB shared vendor id.</summary>
    public const ushort VendorId = 0x16C0;

    /// <summary>V-USB shared HID product id.</summary>
    public const ushort ProductId = 0x05DF;

    /// <summary>Relay count from the product string ("USBRelay2" → 2).</summary>
    public int Relays => device.Info?.Product is { } p && p.StartsWith("USBRelay", StringComparison.Ordinal) && int.TryParse(p.AsSpan(8), out var n) ? n : 1;

    /// <summary>Board serial (5 characters).</summary>
    public async Task<string> GetSerialAsync(CancellationToken ct = default) => Encoding.ASCII.GetString((await device.GetFeatureAsync(0, 9, ct).ConfigureAwait(false)).AsSpan(1, 5)).TrimEnd('\0', ' ');

    /// <summary>Relay states (index 0 = relay 1).</summary>
    public async Task<bool[]> GetStatesAsync(CancellationToken ct = default)
    {
        var r = await device.GetFeatureAsync(0, 9, ct).ConfigureAwait(false);
        var state = r.Length > 8 ? r[8] : (byte)0;
        return [.. Enumerable.Range(0, Relays).Select(i => (state & (1 << i)) != 0)];
    }

    /// <summary>Switches one relay (1-based); refused when the HID device is read-only.</summary>
    public Task SetAsync(int relay, bool on, CancellationToken ct = default)
    {
        if (relay < 1 || relay > Relays) throw new ArgumentOutOfRangeException(nameof(relay), $"The board has relays 1–{Relays}.");
        return device.SendFeatureAsync(new byte[] { 0x00, on ? (byte)0xFF : (byte)0xFD, (byte)relay, 0, 0, 0, 0, 0, 0 }, ct);
    }

    /// <summary>Switches every relay.</summary>
    public Task SetAllAsync(bool on, CancellationToken ct = default) => device.SendFeatureAsync(new byte[] { 0x00, on ? (byte)0xFE : (byte)0xFC, 0, 0, 0, 0, 0, 0, 0 }, ct);
}

/// <summary>A byte stream over a bulk IN/OUT endpoint pair (for USB serial bridges and vendor protocols).</summary>
public sealed class UsbBulkTransport(IUsbBackend backend, string deviceId, byte inEndpoint, byte outEndpoint, byte iface) : StreamTransport
{
    private IUsbDeviceHandle? _handle;

    /// <inheritdoc />
    public override TransportInfo Info => new(TransportKind.Usb, null, $"{deviceId} 0x{inEndpoint:x2}/0x{outEndpoint:x2}");

    /// <inheritdoc />
    protected override ValueTask<Stream> OpenStreamAsync(CancellationToken ct)
    {
        _handle = backend.Open(deviceId);
        _handle.Claim(iface, detachKernelDriver: true);
        return ValueTask.FromResult<Stream>(new BulkStream(_handle, inEndpoint, outEndpoint));
    }

    /// <inheritdoc />
    protected override ValueTask CloseCoreAsync()
    {
        _handle?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class BulkStream(IUsbDeviceHandle h, byte inEp, byte outEp) : Stream
    {
        private byte[] _pending = [];
        private int _offset;
        private volatile bool _closed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_offset >= _pending.Length)
            {
                if (_closed) return 0;
                var data = h.Read(inEp, interrupt: false, 512, TimeSpan.FromMilliseconds(200));
                if (data is { Length: > 0 }) (_pending, _offset) = (data, 0);
            }

            var n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => Read(buffer.Span), cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => h.Write(outEp, interrupt: false, buffer.AsSpan(offset, count), TimeSpan.FromSeconds(2));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => h.Write(outEp, interrupt: false, buffer.Span, TimeSpan.FromSeconds(2)), cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed = true;
            base.Dispose(disposing);
        }
    }
}

/// <summary><c>UseUsbBulk</c> builder extension for every IoTCom.Net endpoint.</summary>
public static class UsbBuilderExtensions
{
    /// <summary>Connects through a USB bulk endpoint pair (default IN 0x81, OUT 0x01 on interface 0).</summary>
    public static T UseUsbBulk<T>(this T builder, string deviceId, byte inEndpoint = 0x81, byte outEndpoint = 0x01, byte iface = 0, IUsbBackend? backend = null)
        where T : ITransportBuilder<T>
        => builder.UseTransport(() => new UsbBulkTransport(backend ?? NativeUsbBackend.Instance, deviceId, inEndpoint, outEndpoint, iface));
}
