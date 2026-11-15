using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using IoTCom.Net.Transport.Usb;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Debug;

/// <summary>DFU class requests (DFU 1.1).</summary>
public enum DfuRequest : byte
{
    /// <summary>Switch from run-time to DFU mode.</summary>
    Detach = 0,
    /// <summary>Download a block.</summary>
    Download = 1,
    /// <summary>Upload a block.</summary>
    Upload = 2,
    /// <summary>Status, poll timeout and state.</summary>
    GetStatus = 3,
    /// <summary>Clear an error status.</summary>
    ClearStatus = 4,
    /// <summary>State only.</summary>
    GetState = 5,
    /// <summary>Back to dfuIDLE.</summary>
    Abort = 6,
}

/// <summary>DFU states.</summary>
public enum DfuState : byte
{
    /// <summary>Run-time, idle.</summary>
    AppIdle = 0,
    /// <summary>Run-time, detach requested.</summary>
    AppDetach = 1,
    /// <summary>DFU mode, idle.</summary>
    DfuIdle = 2,
    /// <summary>Block received, waiting for GETSTATUS.</summary>
    DownloadSync = 3,
    /// <summary>Programming.</summary>
    DownloadBusy = 4,
    /// <summary>Ready for the next block.</summary>
    DownloadIdle = 5,
    /// <summary>Last block received, waiting for GETSTATUS.</summary>
    ManifestSync = 6,
    /// <summary>Manifesting.</summary>
    Manifest = 7,
    /// <summary>Waiting for a USB reset.</summary>
    ManifestWaitReset = 8,
    /// <summary>Uploading.</summary>
    UploadIdle = 9,
    /// <summary>Error; clear with CLRSTATUS.</summary>
    Error = 10,
}

/// <summary>A GETSTATUS reply.</summary>
/// <param name="Status">bStatus (0 OK; 3 errWRITE, 4 errERASE, 6 errPROG, 7 errVERIFY, 8 errADDRESS, 10 errUNKNOWN…).</param>
/// <param name="PollTimeout">Wait before the next GETSTATUS.</param>
/// <param name="State">bState.</param>
public readonly record struct DfuStatus(byte Status, TimeSpan PollTimeout, DfuState State)
{
    /// <summary>Parses the 6-byte reply.</summary>
    public static DfuStatus Parse(ReadOnlySpan<byte> b) => b.Length < 6
        ? throw new ProtocolException($"GETSTATUS returned {b.Length} bytes; expected 6.")
        : new(b[0], TimeSpan.FromMilliseconds(b[1] | (b[2] << 8) | (b[3] << 16)), (DfuState)b[4]);

    /// <summary>Encodes the reply.</summary>
    public byte[] Encode()
    {
        var ms = (int)PollTimeout.TotalMilliseconds;
        return [Status, (byte)ms, (byte)(ms >> 8), (byte)(ms >> 16), (byte)State, 0];
    }

    /// <summary>Text for the status code.</summary>
    public string StatusText => Status switch
    {
        0 => "OK",
        1 => "errTARGET",
        2 => "errFILE",
        3 => "errWRITE",
        4 => "errERASE",
        5 => "errCHECK_ERASED",
        6 => "errPROG",
        7 => "errVERIFY",
        8 => "errADDRESS",
        9 => "errNOTDONE",
        10 => "errFIRMWARE",
        11 => "errVENDOR",
        14 => "errUNKNOWN",
        15 => "errSTALLEDPKT",
        _ => $"0x{Status:X2}",
    };
}

/// <summary>One memory region from a DfuSe alternate-setting string.</summary>
/// <param name="Start">Start address.</param>
/// <param name="PageCount">Pages.</param>
/// <param name="PageSize">Page size in bytes.</param>
/// <param name="Readable">Readable (a, c, e, g).</param>
/// <param name="Erasable">Erasable (b, c, f, g).</param>
/// <param name="Writable">Writable (d, e, f, g).</param>
public readonly record struct DfuSeRegion(uint Start, int PageCount, int PageSize, bool Readable, bool Erasable, bool Writable)
{
    /// <summary>First address after the region.</summary>
    public ulong End => Start + ((ulong)PageCount * (uint)PageSize);
}

/// <summary>A DfuSe memory layout such as <c>@Internal Flash  /0x08000000/04*016Kg,01*064Kg,03*128Kg</c>.</summary>
/// <param name="Name">Memory name.</param>
/// <param name="Regions">Regions in address order.</param>
public sealed partial record DfuSeLayout(string Name, IReadOnlyList<DfuSeRegion> Regions)
{
    /// <summary>Parses a layout string; null when it is not one.</summary>
    public static DfuSeLayout? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '@') return null;
        var parts = text[1..].Split('/');
        if (parts.Length < 3) return null;
        var regions = new List<DfuSeRegion>();
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            if (!uint.TryParse(parts[i].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)) return null;
            foreach (var spec in parts[i + 1].Split(','))
            {
                var m = Sector().Match(spec.Trim());
                if (!m.Success) return null;
                var count = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var size = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * (m.Groups[3].Value switch { "K" => 1024, "M" => 1024 * 1024, _ => 1 });
                var t = m.Groups[4].Value[0] - 'a' + 1;   // a=1 … g=7: bit 0 readable, bit 1 erasable, bit 2 writable
                regions.Add(new DfuSeRegion(start, count, size, (t & 1) != 0, (t & 2) != 0, (t & 4) != 0));
                start += (uint)(count * size);
            }
        }

        return new DfuSeLayout(parts[0].Trim(), regions);
    }

    /// <summary>The region holding <paramref name="address"/>.</summary>
    public DfuSeRegion? Find(uint address) => Regions.FirstOrDefault(r => address >= r.Start && address < r.End) is { PageSize: > 0 } r ? r : null;

    /// <summary>Normalised description.</summary>
    public string Describe() => $"{Name}: " + string.Join(',', Regions.Select(r => string.Create(CultureInfo.InvariantCulture,
        $"{r.Start:X8}+{r.PageCount}x{r.PageSize}{(r.Readable ? "r" : "")}{(r.Erasable ? "e" : "")}{(r.Writable ? "w" : "")}")));

    [GeneratedRegex(@"^(\d+)\*(\d+)([ KM])([a-g])$")]
    private static partial Regex Sector();
}

/// <summary>Options for <see cref="DfuClient"/>.</summary>
public sealed class DfuClientOptions
{
    /// <summary>USB backend (default: native).</summary>
    public IUsbBackend Backend { get; set; } = NativeUsbBackend.Instance;

    /// <summary>Device id <c>vvvv:pppp[:serial]</c> (default 0483:df11, the STM32 system bootloader).</summary>
    public string DeviceId { get; set; } = "0483:df11";

    /// <summary>DFU interface number.</summary>
    public byte Interface { get; set; }

    /// <summary>Transfer size per block (default 2048; DfuSe devices report theirs in the functional descriptor).</summary>
    public int TransferSize { get; set; } = 2048;

    /// <summary>Read-only (default): status and upload only.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Timeout per transfer.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Selects a device.</summary>
    public DfuClientOptions UseDevice(ushort vendorId, ushort productId, string? serial = null)
    {
        DeviceId = $"{vendorId:x4}:{productId:x4}{(serial is null ? "" : ":" + serial)}";
        return this;
    }

    /// <summary>Uses a virtual bus.</summary>
    public DfuClientOptions UseVirtual(VirtualUsbBus bus)
    {
        Backend = bus;
        return this;
    }

    /// <summary>Allows download, erase and leave.</summary>
    public DfuClientOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// A USB DFU 1.1 host with the ST DfuSe extensions: status, upload, download of plain and DfuSe images (page erase,
/// set address, blocks, manifest/leave). Read-only by default: downloads and erases need
/// <see cref="DfuClientOptions.AllowWrites"/>.
/// </summary>
public sealed class DfuClient : IAsyncDisposable
{
    private readonly DfuClientOptions _options;
    private readonly ILogger? _logger;
    private UsbDevice? _usb;

    private DfuClient(DfuClientOptions options) => (_options, _logger) = (options, options.Logger);

    /// <summary>Creates a client.</summary>
    public static DfuClient Create(Action<DfuClientOptions> configure)
    {
        var o = new DfuClientOptions();
        configure(o);
        return new DfuClient(o);
    }

    /// <summary>The device's DfuSe memory layout (from the interface string), when it has one.</summary>
    public DfuSeLayout? Layout { get; private set; }

    /// <summary>The device as enumerated.</summary>
    public UsbDeviceInfo? Device { get; private set; }

    /// <summary>The USB endpoint (traffic tap, state).</summary>
    public UsbDevice? Usb => _usb;

    /// <summary>True when writes are allowed.</summary>
    public bool WritesAllowed => !_options.ReadOnly;

    /// <summary>Opens the device and reads its layout.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_usb is not null) return;
        Device = _options.Backend.List().FirstOrDefault(d => d.Id.StartsWith(_options.DeviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeviceException($"No DFU device {_options.DeviceId}: put the board in DFU mode (STM32: BOOT0 high, reset).");
        var iface = Device.Interfaces.FirstOrDefault(i => i.Number == _options.Interface);
        if (iface is not null && iface.Class != 0xFE) throw new DeviceException($"Interface {_options.Interface} of {Device.Id} is class 0x{iface.Class:X2}, not DFU (0xFE).");
        Layout = DfuSeLayout.Parse(iface?.Name);
        var usb = UsbDevice.Create(o =>
        {
            o.Backend = _options.Backend;
            o.DeviceId = _options.DeviceId;
            o.Interfaces.Clear();
            o.Interfaces.Add(_options.Interface);
            o.Timeout = _options.Timeout;
            o.Name = _options.Name ?? "dfu";
        });
        await usb.ConnectAsync(ct).ConfigureAwait(false);
        _usb = usb;
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.State == DfuState.Error) await ClearStatusAsync(ct).ConfigureAwait(false);
        else if (status.State is not (DfuState.DfuIdle or DfuState.AppIdle)) await AbortAsync(ct).ConfigureAwait(false);
    }

    private UsbDevice Usb_ => _usb ?? throw new InvalidOperationException("Call ConnectAsync first.");

    private UsbSetup Out(DfuRequest r, ushort value = 0) => new(0x21, (byte)r, value, _options.Interface);

    private UsbSetup In(DfuRequest r, ushort value = 0) => new(0xA1, (byte)r, value, _options.Interface);

    /// <summary>GETSTATUS.</summary>
    public async Task<DfuStatus> GetStatusAsync(CancellationToken ct = default) => DfuStatus.Parse(await Usb_.ControlInAsync(In(DfuRequest.GetStatus), 6, ct).ConfigureAwait(false));

    /// <summary>CLRSTATUS.</summary>
    public Task ClearStatusAsync(CancellationToken ct = default) => Usb_.ControlOutAsync(Out(DfuRequest.ClearStatus), default, ct);

    /// <summary>ABORT.</summary>
    public Task AbortAsync(CancellationToken ct = default) => Usb_.ControlOutAsync(Out(DfuRequest.Abort), default, ct);

    private async Task<DfuStatus> WaitIdleAsync(string what, CancellationToken ct)
    {
        for (var i = 0; i < 1000; i++)
        {
            var s = await GetStatusAsync(ct).ConfigureAwait(false);
            if (s.Status != 0)
            {
                await ClearStatusAsync(ct).ConfigureAwait(false);
                throw new DeviceException($"DFU {what} failed: {s.StatusText}.");
            }

            if (s.State is DfuState.DownloadIdle or DfuState.DfuIdle or DfuState.ManifestWaitReset or DfuState.Manifest or DfuState.ManifestSync && i > 0) return s;
            if (s.State is DfuState.DownloadBusy or DfuState.DownloadSync or DfuState.ManifestSync) await Task.Delay(s.PollTimeout, ct).ConfigureAwait(false);
            else return s;
        }

        throw new IoTComTimeoutException($"DFU {what} did not finish.");
    }

    private void RequireWrites(string what)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"{what} changes the device's firmware; this DFU client is read-only until AllowWrites().");
    }

    private async Task DfuSeCommandAsync(byte command, uint? address, string what, CancellationToken ct)
    {
        var data = address is { } a ? new byte[] { command, (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24) } : [command];
        await Usb_.ControlOutAsync(Out(DfuRequest.Download, 0), data, ct).ConfigureAwait(false);
        await WaitIdleAsync(what, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads an image: DfuSe (erase pages, set address, blocks) when the device has a layout, plain DFU otherwise.</summary>
    public async Task DownloadAsync(FirmwareImage image, IProgress<FlashProgress>? progress = null, CancellationToken ct = default)
    {
        RequireWrites("Downloading firmware");
        await ConnectAsync(ct).ConfigureAwait(false);
        var xfer = _options.TransferSize;
        if (Layout is null)
        {
            var data = image.Flatten();
            var blocks = (data.Length + xfer - 1) / xfer;
            for (var n = 0; n < blocks; n++)
            {
                await Usb_.ControlOutAsync(Out(DfuRequest.Download, (ushort)n), data.AsMemory(n * xfer, Math.Min(xfer, data.Length - (n * xfer))), ct).ConfigureAwait(false);
                await WaitIdleAsync($"block {n}", ct).ConfigureAwait(false);
                progress?.Report(new FlashProgress("Programming", (n + 1) / (double)blocks));
            }

            await Usb_.ControlOutAsync(Out(DfuRequest.Download, (ushort)blocks), default, ct).ConfigureAwait(false);
            await WaitIdleAsync("manifest", ct).ConfigureAwait(false);
            return;
        }

        foreach (var s in image.Segments)
        {
            var region = Layout.Find(s.Address) ?? throw new DeviceException($"0x{s.Address:X8} is not in the device's memory ({Layout.Describe()}).");
            if (s.End > region.End || !region.Writable) throw new DeviceException($"Segment 0x{s.Address:X8}–0x{s.End:X8} does not fit a writable region of {Layout.Name}.");
        }

        var pages = image.Segments.SelectMany(s =>
        {
            var r = Layout.Find(s.Address)!.Value;
            var first = (s.Address - r.Start) / (uint)r.PageSize;
            var last = (uint)((s.End - 1 - r.Start) / (uint)r.PageSize);
            return Enumerable.Range((int)first, (int)(last - first + 1)).Select(p => r.Start + ((uint)p * (uint)r.PageSize));
        }).Distinct().ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            await DfuSeCommandAsync(0x41, pages[i], $"erase of 0x{pages[i]:X8}", ct).ConfigureAwait(false);
            progress?.Report(new FlashProgress("Erasing", (i + 1) / (double)pages.Count));
        }

        long done = 0;
        foreach (var s in image.Segments)
        {
            for (var offset = 0; offset < s.Data.Length; offset += xfer)
            {
                var address = s.Address + (uint)offset;
                await DfuSeCommandAsync(0x21, address, $"set address 0x{address:X8}", ct).ConfigureAwait(false);
                var chunk = s.Data.AsMemory(offset, Math.Min(xfer, s.Data.Length - offset));
                await Usb_.ControlOutAsync(Out(DfuRequest.Download, 2), chunk, ct).ConfigureAwait(false);
                await WaitIdleAsync($"write at 0x{address:X8}", ct).ConfigureAwait(false);
                done += chunk.Length;
                progress?.Report(new FlashProgress("Programming", done / (double)image.Size));
            }
        }

        _logger?.LogInformation("DfuSe download of {Image} done", image);
    }

    /// <summary>Uploads <paramref name="length"/> bytes from <paramref name="address"/> (DfuSe) or from the start (plain DFU).</summary>
    public async Task<byte[]> UploadAsync(int length, uint? address = null, CancellationToken ct = default)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        var xfer = _options.TransferSize;
        var result = new List<byte>();
        if (Layout is not null && address is { } a)
        {
            if (!_options.ReadOnly) await AbortAsync(ct).ConfigureAwait(false);
            await Usb_.ControlOutAsync(Out(DfuRequest.Download, 0), new byte[] { 0x21, (byte)a, (byte)(a >> 8), (byte)(a >> 16), (byte)(a >> 24) }, ct).ConfigureAwait(false);
            await WaitIdleAsync("set address", ct).ConfigureAwait(false);
            await AbortAsync(ct).ConfigureAwait(false);
            for (var n = 0; result.Count < length; n++)
            {
                var block = await Usb_.ControlInAsync(In(DfuRequest.Upload, (ushort)(n + 2)), Math.Min(xfer, length - result.Count), ct).ConfigureAwait(false);
                if (block.Length == 0) break;
                result.AddRange(block);
            }
        }
        else
        {
            for (var n = 0; result.Count < length; n++)
            {
                var block = await Usb_.ControlInAsync(In(DfuRequest.Upload, (ushort)n), Math.Min(xfer, length - result.Count), ct).ConfigureAwait(false);
                result.AddRange(block);
                if (block.Length < Math.Min(xfer, length - result.Count + block.Length)) break;
            }
        }

        await AbortAsync(ct).ConfigureAwait(false);
        return [.. result];
    }

    /// <summary>DfuSe "leave DFU mode": jump to the application at <paramref name="address"/>.</summary>
    public async Task LeaveAsync(uint address = 0x08000000, CancellationToken ct = default)
    {
        RequireWrites("Leaving DFU mode");
        await ConnectAsync(ct).ConfigureAwait(false);
        await DfuSeCommandAsync(0x21, address, "set address", ct).ConfigureAwait(false);
        await Usb_.ControlOutAsync(Out(DfuRequest.Download, 0), default, ct).ConfigureAwait(false);
        try
        {
            await GetStatusAsync(ct).ConfigureAwait(false);   // triggers the manifest; the device then leaves the bus
        }
        catch (Exception ex) when (ex is IoTComException)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_usb is not null) await _usb.DisposeAsync().ConfigureAwait(false);
        _usb = null;
    }
}

/// <summary>
/// A simulated STM32 system bootloader in DFU mode (0483:df11, DfuSe 1.1a) writing the flash of a
/// <see cref="VirtualMcu"/>: page erase, set address, block writes (refused on non-erased flash, like the real
/// controller), uploads, GETSTATUS with poll timeouts, and leave (the MCU resets into the new firmware).
/// </summary>
public sealed class VirtualDfuDevice : VirtualUsbDevice
{
    private const int Xfer = 2048;
    private readonly VirtualMcu _mcu;
    private DfuState _state = DfuState.DfuIdle;
    private byte _status;
    private uint _address = VirtualMcu.FlashBase;
    private int _busyPolls;
    private bool _leavePending;

    /// <summary>Creates a bootloader over <paramref name="mcu"/>.</summary>
    public VirtualDfuDevice(VirtualMcu mcu, string serial = "3276345A3138")
    {
        _mcu = mcu;
        Info = new UsbDeviceInfo
        {
            Id = $"0483:df11:{serial}", VendorId = 0x0483, ProductId = 0xDF11, Manufacturer = "STMicroelectronics", Product = "DFU in FS Mode", Serial = serial,
            Class = 0, Bus = "virtual", Address = 7,
            Interfaces = [new UsbInterfaceInfo(0, 0xFE, 0x01, 0x02, $"@Internal Flash  /0x{VirtualMcu.FlashBase:X8}/{VirtualMcu.FlashSize / VirtualMcu.PageSize:D3}*002Kg")],
        };
    }

    /// <inheritdoc />
    public override UsbDeviceInfo Info { get; }

    /// <summary>True after a leave request (the MCU was reset into its application).</summary>
    public bool Left { get; private set; }

    /// <summary>Blocks written.</summary>
    public int BlocksWritten { get; private set; }

    /// <summary>Pages erased.</summary>
    public int PagesErased { get; private set; }

    /// <inheritdoc />
    protected override byte[]? ControlIn(UsbSetup setup, int length)
    {
        if (setup.RequestType != 0xA1) return null;
        switch ((DfuRequest)setup.Request)
        {
            case DfuRequest.GetStatus:
                var poll = TimeSpan.Zero;
                if (_state is DfuState.DownloadSync or DfuState.DownloadBusy)
                {
                    if (_busyPolls-- > 0)
                    {
                        _state = DfuState.DownloadBusy;
                        poll = TimeSpan.FromMilliseconds(2);
                    }
                    else _state = DfuState.DownloadIdle;
                }
                else if (_state == DfuState.ManifestSync)
                {
                    _state = DfuState.Manifest;
                    if (_leavePending)
                    {
                        Left = true;
                        _mcu.Reset();
                    }
                }

                return new DfuStatus(_status, poll, _state).Encode();
            case DfuRequest.GetState:
                return [(byte)_state];
            case DfuRequest.Upload:
                if (_state is not (DfuState.DfuIdle or DfuState.UploadIdle)) return Fail(DfuState.Error, 0x0F);
                if (setup.Value < 2) return [0x00, 0x21, 0x41, 0x92];   // DfuSe GET command list
                _state = DfuState.UploadIdle;
                var address = _address + (uint)((setup.Value - 2) * Xfer);
                try
                {
                    return _mcu.Read(address, Math.Min(length, (int)Math.Max(0, (long)VirtualMcu.FlashBase + VirtualMcu.FlashSize - address)));
                }
                catch (DeviceException)
                {
                    return Fail(DfuState.Error, 8);
                }

            default:
                return null;
        }
    }

    private byte[]? Fail(DfuState state, byte status)
    {
        (_state, _status) = (state, status);
        return null;
    }

    /// <inheritdoc />
    protected override bool ControlOut(UsbSetup setup, byte[] data)
    {
        if (setup.RequestType != 0x21) return false;
        switch ((DfuRequest)setup.Request)
        {
            case DfuRequest.ClearStatus:
                (_state, _status) = (DfuState.DfuIdle, 0);
                return true;
            case DfuRequest.Abort:
                if (_state != DfuState.Error) _state = DfuState.DfuIdle;
                return true;
            case DfuRequest.Download:
                if (_state is not (DfuState.DfuIdle or DfuState.DownloadIdle)) return Fail(DfuState.Error, 0x0F) is not null;
                if (data.Length == 0)
                {
                    _state = DfuState.ManifestSync;
                    _leavePending = true;
                    return true;
                }

                if (setup.Value == 0)
                {
                    switch (data[0])
                    {
                        case 0x21 when data.Length == 5:
                            _address = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
                            break;
                        case 0x41 when data.Length == 5:
                            if (!_mcu.ErasePage(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1)))) return Fail(DfuState.Error, 8) is not null;
                            PagesErased++;
                            _busyPolls = 2;
                            break;
                        case 0x41 when data.Length == 1:
                            _mcu.EraseAll();
                            _busyPolls = 5;
                            break;
                        default:
                            return Fail(DfuState.Error, 14) is not null;
                    }
                }
                else if (setup.Value >= 2)
                {
                    var address = _address + (uint)((setup.Value - 2) * Xfer);
                    if (!_mcu.ProgramRaw(address, data)) return Fail(DfuState.Error, 6) is not null;   // errPROG: not erased or outside flash
                    BlocksWritten++;
                    _busyPolls = 1;
                }
                else return Fail(DfuState.Error, 14) is not null;

                _state = DfuState.DownloadSync;
                return true;
            default:
                return false;
        }
    }
}
