using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Debug;

/// <summary>A debug probe found on the host.</summary>
/// <param name="Selector">What to pass to <see cref="DebugProbeOptions.UseProbe"/> (<c>vid:pid[:serial]</c>).</param>
/// <param name="Name">Probe name.</param>
/// <param name="Kind">CMSIS-DAP, ST-Link, J-Link, ESP-JTAG, FTDI, WCH-Link…</param>
/// <param name="Serial">Serial number.</param>
public sealed record ProbeInfo(string Selector, string Name, string Kind, string? Serial);

/// <summary>Wire protocol to the target.</summary>
public enum ProbeProtocol
{
    /// <summary>Serial Wire Debug (ARM).</summary>
    Swd,
    /// <summary>JTAG.</summary>
    Jtag,
}

/// <summary>Core state.</summary>
public enum CoreState
{
    /// <summary>Not known.</summary>
    Unknown,
    /// <summary>Running.</summary>
    Running,
    /// <summary>Halted by the debugger.</summary>
    Halted,
    /// <summary>Sleeping (WFI/WFE).</summary>
    Sleeping,
    /// <summary>Locked up (for example erased flash: no valid vector table).</summary>
    LockedUp,
}

/// <summary>Flashing progress.</summary>
/// <param name="Phase">Erasing, Programming, Verifying.</param>
/// <param name="Fraction">0–1 within the phase.</param>
public readonly record struct FlashProgress(string Phase, double Fraction);

/// <summary>Probe access: the native probe-rs library or a simulated MCU.</summary>
public interface IProbeBackend
{
    /// <summary>Lists probes.</summary>
    IReadOnlyList<ProbeInfo> List();

    /// <summary>Attaches to <paramref name="target"/> (a probe-rs target name such as <c>STM32F411CEUx</c>) through a probe.</summary>
    IProbeSession Open(string? selector, string target, ProbeProtocol protocol, int speedKhz);
}

/// <summary>An attached target. Calls block; a session is not thread-safe.</summary>
public interface IProbeSession : IDisposable
{
    /// <summary>Probe name.</summary>
    string Probe { get; }

    /// <summary>Target name.</summary>
    string Target { get; }

    /// <summary>Reads memory.</summary>
    byte[] Read(ulong address, int length);

    /// <summary>Writes memory (RAM and peripherals).</summary>
    void Write(ulong address, ReadOnlySpan<byte> data);

    /// <summary>Halts the core.</summary>
    void Halt();

    /// <summary>Resumes the core.</summary>
    void Run();

    /// <summary>Resets the target, optionally halting at the reset vector.</summary>
    void Reset(bool halt);

    /// <summary>Core state and program counter.</summary>
    (CoreState State, uint Pc) Core();

    /// <summary>Erases what the image needs, programs and verifies it.</summary>
    void Flash(FirmwareImage image, Action<FlashProgress>? progress);

    /// <summary>Erases the whole flash.</summary>
    void EraseAll();
}

/// <summary>Options for <see cref="DebugProbe"/>.</summary>
public sealed class DebugProbeOptions
{
    /// <summary>Backend (default: the native probe-rs library).</summary>
    public IProbeBackend Backend { get; set; } = NativeProbeBackend.Instance;

    /// <summary>Probe selector (null = the only probe).</summary>
    public string? Selector { get; set; }

    /// <summary>probe-rs target name, e.g. <c>STM32F411CEUx</c>, <c>nRF52840_xxAA</c>, <c>RP2040</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Protocol (default SWD).</summary>
    public ProbeProtocol Protocol { get; set; } = ProbeProtocol.Swd;

    /// <summary>Clock in kHz (default 4000).</summary>
    public int SpeedKhz { get; set; } = 4000;

    /// <summary>Read-only (default): memory reads, core status and RTT reads only.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Selects a probe.</summary>
    public DebugProbeOptions UseProbe(string selector)
    {
        Selector = selector;
        return this;
    }

    /// <summary>Selects the target chip.</summary>
    public DebugProbeOptions UseTarget(string target)
    {
        Target = target;
        return this;
    }

    /// <summary>Uses a simulated MCU.</summary>
    public DebugProbeOptions UseVirtual(VirtualProbeBackend backend)
    {
        Backend = backend;
        return this;
    }

    /// <summary>Allows flashing, erasing, memory writes, halt, run and reset.</summary>
    public DebugProbeOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// A debug probe attached to a microcontroller over SWD or JTAG: memory reads, core status, RTT logs, and — after
/// <see cref="DebugProbeOptions.AllowWrites"/> — flashing (ELF, Intel HEX, binary, DFU files), erase, memory writes,
/// halt, run and reset. The native backend wraps probe-rs (CMSIS-DAP, ST-Link, J-Link, ESP USB-JTAG, FTDI, WCH-Link).
/// </summary>
public sealed class DebugProbe : EndpointBase, IClientEndpoint, ITargetMemory
{
    private readonly DebugProbeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IProbeSession? _session;

    private DebugProbe(DebugProbeOptions options) : base("swd", options.Logger) => _options = options;

    /// <summary>Creates a probe endpoint.</summary>
    public static DebugProbe Create(Action<DebugProbeOptions> configure)
    {
        var o = new DebugProbeOptions();
        configure(o);
        if (string.IsNullOrWhiteSpace(o.Target)) throw new ArgumentException("Set the target chip with UseTarget(\"STM32F411CEUx\") (probe-rs target names).", nameof(configure));
        return new DebugProbe(o) { Name = o.Name };
    }

    /// <summary>Probes on the default backend.</summary>
    public static IReadOnlyList<ProbeInfo> List() => NativeProbeBackend.Instance.List();

    /// <summary>True when writes are allowed.</summary>
    public bool WritesAllowed => !_options.ReadOnly;

    /// <summary>Target name.</summary>
    public string Target => _session?.Target ?? _options.Target;

    /// <summary>Probe name.</summary>
    public string? ProbeName => _session?.Probe;

    /// <summary>Attaches to the target.</summary>
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_session is not null) return;
        SetState(EndpointState.Connecting);
        try
        {
            _session = await Task.Run(() => _options.Backend.Open(_options.Selector, _options.Target, _options.Protocol, _options.SpeedKhz), ct).ConfigureAwait(false);
            Logger.LogInformation("Attached to {Target} through {Probe}", _session.Target, _session.Probe);
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    /// <summary>Detaches (the target keeps running).</summary>
    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        _session?.Dispose();
        _session = null;
        SetState(EndpointState.Disconnected);
        return ValueTask.CompletedTask;
    }

    private async Task<T> Run<T>(Func<IProbeSession, T> call, CancellationToken ct)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => call(_session!), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RequireWrites(string what)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"{what} changes the target; this probe is read-only until AllowWrites().");
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken ct = default)
    {
        var data = await Run(s => s.Read(address, length), ct).ConfigureAwait(false);
        Tap(FrameDirection.Inbound, data, () => string.Create(CultureInfo.InvariantCulture, $"read 0x{address:X8} [{length}]"));
        return data;
    }

    /// <summary>Reads a 32-bit little-endian word.</summary>
    public async Task<uint> ReadUInt32Async(ulong address, CancellationToken ct = default) => BitConverter.ToUInt32(await ReadMemoryAsync(address, 4, ct).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task WriteMemoryAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        RequireWrites($"Writing 0x{address:X8}");
        Tap(FrameDirection.Outbound, data.Span, () => string.Create(CultureInfo.InvariantCulture, $"write 0x{address:X8} [{data.Length}]"));
        await Run(s =>
        {
            s.Write(address, data.Span);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Internal: RTT bookkeeping writes (read offsets) are allowed in read-only mode.</summary>
    internal Task WriteRttBookkeepingAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct) => Run(s =>
    {
        s.Write(address, data.Span);
        return 0;
    }, ct);

    /// <summary>Core state and PC.</summary>
    public Task<(CoreState State, uint Pc)> CoreAsync(CancellationToken ct = default) => Run(s => s.Core(), ct);

    /// <summary>Halts the core.</summary>
    public Task HaltAsync(CancellationToken ct = default)
    {
        RequireWrites("Halting");
        return Run(s =>
        {
            s.Halt();
            return 0;
        }, ct);
    }

    /// <summary>Resumes the core.</summary>
    public Task RunAsync(CancellationToken ct = default)
    {
        RequireWrites("Resuming");
        return Run(s =>
        {
            s.Run();
            return 0;
        }, ct);
    }

    /// <summary>Resets the target.</summary>
    public Task ResetAsync(bool halt = false, CancellationToken ct = default)
    {
        RequireWrites("Resetting");
        return Run(s =>
        {
            s.Reset(halt);
            return 0;
        }, ct);
    }

    /// <summary>Erases, programs and verifies an image, then resets the target.</summary>
    public async Task FlashAsync(FirmwareImage image, IProgress<FlashProgress>? progress = null, bool reset = true, CancellationToken ct = default)
    {
        RequireWrites("Flashing");
        Logger.LogInformation("Flashing {Image} to {Target}", image, Target);
        await Run(s =>
        {
            s.Flash(image, p => progress?.Report(p));
            if (reset) s.Reset(halt: false);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Erases the whole flash.</summary>
    public Task EraseAllAsync(CancellationToken ct = default)
    {
        RequireWrites("Erasing");
        return Run(s =>
        {
            s.EraseAll();
            return 0;
        }, ct);
    }

    /// <summary>Attaches an RTT host (down writes need <see cref="DebugProbeOptions.AllowWrites"/>).</summary>
    public Task<RttClient> AttachRttAsync(ulong ramStart, int ramLength, ulong? controlBlock = null, CancellationToken ct = default)
        => RttClient.AttachAsync(new RttMemory(this), ramStart, ramLength, controlBlock, WritesAllowed, ct);

    private sealed class RttMemory(DebugProbe probe) : ITargetMemory
    {
        public Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken ct = default) => probe.ReadMemoryAsync(address, length, ct);

        public Task WriteMemoryAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => probe.WritesAllowed ? probe.WriteMemoryAsync(address, data, ct) : probe.WriteRttBookkeepingAsync(address, data, ct);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>
/// A simulated Cortex-M microcontroller (STM32F411-like: 256 KiB flash at 0x08000000 in 2 KiB pages, 128 KiB RAM at
/// 0x20000000) running IoTCom's demo firmware: after reset it sets up an RTT control block and logs boot, sensor and
/// command lines; its version comes from the <c>FWVER:</c> tag in flash. Erased flash locks the core up.
/// </summary>
public sealed class VirtualMcu
{
    /// <summary>Flash base.</summary>
    public const uint FlashBase = 0x08000000;
    /// <summary>Flash size.</summary>
    public const int FlashSize = 256 * 1024;
    /// <summary>Flash page size.</summary>
    public const int PageSize = 2048;
    /// <summary>RAM base.</summary>
    public const uint RamBase = 0x20000000;
    /// <summary>RAM size.</summary>
    public const int RamSize = 128 * 1024;
    /// <summary>Where the demo firmware places its RTT control block.</summary>
    public const uint RttAddress = 0x20000400;
    /// <summary>Uptime counter (milliseconds, u32) the demo firmware updates.</summary>
    public const uint UptimeAddress = 0x20000000;

    private const uint UpBuffer = 0x20000800, UpSize = 1024, DownBuffer = 0x20000C00, DownSize = 32, NameAddress = 0x20000480;
    private readonly byte[] _flash = new byte[FlashSize];
    private readonly byte[] _ram = new byte[RamSize];
    private readonly Lock _gate = new();
    private double _uptime, _nextLog;
    private int _counter;
    private bool _booted, _led;

    /// <summary>Creates an MCU with erased flash (or the given firmware).</summary>
    public VirtualMcu(string target = "STM32F411CEUx", FirmwareImage? firmware = null)
    {
        Target = target;
        _flash.AsSpan().Fill(0xFF);
        if (firmware is not null) Program(firmware);
        Reset();
    }

    /// <summary>probe-rs target name.</summary>
    public string Target { get; }

    /// <summary>True while halted.</summary>
    public bool Halted { get; private set; }

    /// <summary>Firmware version from the <c>FWVER:</c> tag, or null without valid firmware.</summary>
    public string? FirmwareVersion { get; private set; }

    /// <summary>LED state commanded over RTT ("led on" / "led off").</summary>
    public bool Led => _led;

    /// <summary>Times the flash was programmed.</summary>
    public int FlashCycles { get; private set; }

    /// <summary>Lines logged since reset (also in the RTT buffer).</summary>
    public int LinesLogged { get; private set; }

    /// <summary>True when the vector table is valid (initial SP in RAM, reset vector in flash, Thumb bit set).</summary>
    public bool HasValidFirmware
    {
        get
        {
            var sp = BitConverter.ToUInt32(_flash, 0);
            var pc = BitConverter.ToUInt32(_flash, 4);
            return sp > RamBase && sp <= RamBase + RamSize && (pc & 1) == 1 && pc >= FlashBase && pc < FlashBase + FlashSize;
        }
    }

    /// <summary>Builds a demo firmware image: a vector table, a version tag and some code-like filler.</summary>
    public static FirmwareImage BuildDemoFirmware(string version, int size = 12 * 1024)
    {
        var b = new byte[size];
        var rng = new Random(version.GetHashCode(StringComparison.Ordinal));
        rng.NextBytes(b);
        BitConverter.GetBytes(RamBase + RamSize).CopyTo(b, 0);   // initial stack pointer
        BitConverter.GetBytes(FlashBase + 0x189).CopyTo(b, 4);   // reset handler (Thumb)
        var tag = Encoding.ASCII.GetBytes($"FWVER:{version}\0IoTCom demo firmware\0");
        tag.CopyTo(b, 0x200);
        return new FirmwareImage([new FirmwareSegment(FlashBase, b)], FlashBase + 0x189);
    }

    internal byte[] Read(ulong address, int length)
    {
        lock (_gate)
        {
            if (In(address, length, FlashBase, FlashSize)) return _flash.AsSpan((int)(address - FlashBase), length).ToArray();
            if (In(address, length, RamBase, RamSize)) return _ram.AsSpan((int)(address - RamBase), length).ToArray();
            if (address == 0xE0042000 && length == 4) return BitConverter.GetBytes(0x10006431u);   // DBGMCU_IDCODE (STM32F411)
            throw new DeviceException($"Read of 0x{address:X8} [{length}] is outside flash and RAM: the probe reports a fault.");
        }
    }

    internal void Write(ulong address, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (In(address, data.Length, RamBase, RamSize))
            {
                data.CopyTo(_ram.AsSpan((int)(address - RamBase)));
                return;
            }

            if (In(address, data.Length, FlashBase, FlashSize)) throw new DeviceException($"0x{address:X8} is flash: write it with Flash, not a memory write.");
            throw new DeviceException($"Write to 0x{address:X8} is outside RAM.");
        }
    }

    private static bool In(ulong address, int length, uint start, int size) => address >= start && address + (ulong)length <= start + (ulong)size;

    /// <summary>Erases the pages an image needs, programs it (bits only go 1 → 0, as in real flash) and verifies.</summary>
    internal void Flash(FirmwareImage image, Action<FlashProgress>? progress)
    {
        foreach (var s in image.Segments)
            if (s.Address < FlashBase || s.End > FlashBase + (ulong)FlashSize)
                throw new DeviceException($"Segment 0x{s.Address:X8}–0x{s.End:X8} is outside the flash of {Target} (0x{FlashBase:X8}–0x{FlashBase + FlashSize:X8}).");
        lock (_gate)
        {
            var pages = image.Segments.SelectMany(s => Enumerable.Range((int)((s.Address - FlashBase) / PageSize), (int)(((s.End - 1 - FlashBase) / PageSize) - ((s.Address - FlashBase) / PageSize)) + 1)).Distinct().ToList();
            for (var i = 0; i < pages.Count; i++)
            {
                _flash.AsSpan(pages[i] * PageSize, PageSize).Fill(0xFF);
                progress?.Invoke(new FlashProgress("Erasing", (i + 1) / (double)pages.Count));
            }

            Program(image, progress);
            var total = image.Size;
            long done = 0;
            foreach (var s in image.Segments)
            {
                if (!_flash.AsSpan((int)(s.Address - FlashBase), s.Data.Length).SequenceEqual(s.Data)) throw new DeviceException($"Verify failed at 0x{s.Address:X8}.");
                done += s.Data.Length;
                progress?.Invoke(new FlashProgress("Verifying", done / (double)total));
            }

            FlashCycles++;
        }
    }

    private void Program(FirmwareImage image, Action<FlashProgress>? progress = null)
    {
        var total = image.Size;
        long done = 0;
        foreach (var s in image.Segments)
        {
            var at = (int)(s.Address - FlashBase);
            for (var i = 0; i < s.Data.Length; i++) _flash[at + i] &= s.Data[i];
            done += s.Data.Length;
            progress?.Invoke(new FlashProgress("Programming", done / (double)total));
        }
    }

    /// <summary>Programs bytes without erasing (used by the DFU bootloader, which erases separately).</summary>
    internal bool ProgramRaw(uint address, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (!In(address, data.Length, FlashBase, FlashSize)) return false;
            var at = (int)(address - FlashBase);
            for (var i = 0; i < data.Length; i++)
            {
                if ((_flash[at + i] & data[i]) != data[i]) return false;   // not erased: programming would fail verification
                _flash[at + i] = data[i];
            }

            return true;
        }
    }

    internal bool ErasePage(uint address)
    {
        lock (_gate)
        {
            if (!In(address, 1, FlashBase, FlashSize)) return false;
            var page = (int)((address - FlashBase) / PageSize);
            _flash.AsSpan(page * PageSize, PageSize).Fill(0xFF);
            return true;
        }
    }

    internal void EraseAll()
    {
        lock (_gate) _flash.AsSpan().Fill(0xFF);
        FlashCycles++;
    }

    /// <summary>Resets: clears RAM, and boots the firmware when the vector table is valid.</summary>
    public void Reset(bool halt = false)
    {
        lock (_gate)
        {
            _ram.AsSpan().Clear();
            (_uptime, _nextLog, _counter, _booted, LinesLogged) = (0, 0, 0, false, 0);
            Halted = halt;
            FirmwareVersion = HasValidFirmware ? new FirmwareImage([new FirmwareSegment(FlashBase, _flash)]).FindTag("FWVER:") ?? "unknown" : null;
        }
    }

    internal void SetHalted(bool halted)
    {
        lock (_gate) Halted = halted;
    }

    internal (CoreState, uint) Core()
    {
        lock (_gate)
        {
            if (!HasValidFirmware) return (CoreState.LockedUp, 0xFFFFFFFE);
            var pc = FlashBase + 0x189 + (uint)((int)(_uptime * 1000) % 0x400 & ~1);
            return (Halted ? CoreState.Halted : _booted && _uptime % 1 > 0.2 ? CoreState.Sleeping : CoreState.Running, pc);
        }
    }

    /// <summary>Runs the firmware for <paramref name="seconds"/> (nothing happens while halted or without firmware).</summary>
    public void Step(double seconds)
    {
        lock (_gate)
        {
            if (Halted || !HasValidFirmware) return;
            if (!_booted)
            {
                InitRtt();
                _booted = true;
                Log($"IoTCom demo app {FirmwareVersion} booted on {Target}");
                Log("RTT ready: send 'led on', 'led off' or 'status' on down channel 0");
            }

            _uptime += seconds;
            BitConverter.GetBytes((uint)(_uptime * 1000)).CopyTo(_ram, (int)(UptimeAddress - RamBase));
            ReadDown();
            while (_uptime >= _nextLog)
            {
                var at = _nextLog;
                _nextLog += 1;
                _counter++;
                var temp = 23.4 + (0.6 * Math.Sin(_counter / 5.0));
                Log(string.Create(CultureInfo.InvariantCulture, $"t={at:0.0}s temp={temp:0.0}C counter={_counter} led={(_led ? "on" : "off")}"));
            }
        }
    }

    private void InitRtt()
    {
        var rb = (int)(RttAddress - RamBase);
        Encoding.ASCII.GetBytes("Terminal\0").CopyTo(_ram, (int)(NameAddress - RamBase));
        BitConverter.GetBytes(1).CopyTo(_ram, rb + 16);
        BitConverter.GetBytes(1).CopyTo(_ram, rb + 20);
        uint[] up = [NameAddress, UpBuffer, UpSize, 0, 0, 0];
        uint[] down = [NameAddress, DownBuffer, DownSize, 0, 0, 0];
        for (var i = 0; i < 6; i++)
        {
            BitConverter.GetBytes(up[i]).CopyTo(_ram, rb + 24 + (i * 4));
            BitConverter.GetBytes(down[i]).CopyTo(_ram, rb + 48 + (i * 4));
        }

        RttControlBlock.Id.CopyTo(_ram.AsSpan(rb));   // the ID goes last, as SEGGER's code does, so a host never sees a half-built block
    }

    private uint Word(uint address) => BitConverter.ToUInt32(_ram, (int)(address - RamBase));

    private void SetWord(uint address, uint value) => BitConverter.GetBytes(value).CopyTo(_ram, (int)(address - RamBase));

    private void Log(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        var desc = RttAddress + 24;
        var write = Word(desc + 12);
        var read = Word(desc + 16);
        var free = RttRing.Free(write, read, UpSize);
        var n = (int)Math.Min(free, (uint)bytes.Length);   // mode 0: skip what does not fit
        for (var i = 0; i < n; i++)
        {
            _ram[(int)(UpBuffer - RamBase + write)] = bytes[i];
            write = (write + 1) % UpSize;
        }

        SetWord(desc + 12, write);
        LinesLogged++;
    }

    private string _pending = "";

    private void ReadDown()
    {
        var desc = RttAddress + 48;
        var write = Word(desc + 12);
        var read = Word(desc + 16);
        while (read != write)
        {
            _pending += (char)_ram[(int)(DownBuffer - RamBase + read)];
            read = (read + 1) % DownSize;
        }

        SetWord(desc + 16, read);
        int nl;
        while ((nl = _pending.IndexOfAny(['\n', '\r'])) >= 0)
        {
            var command = _pending[..nl].Trim();
            _pending = _pending[(nl + 1)..];
            if (command.Length == 0) continue;
            switch (command.ToLowerInvariant())
            {
                case "led on":
                    _led = true;
                    Log("LED on");
                    break;
                case "led off":
                    _led = false;
                    Log("LED off");
                    break;
                case "status":
                    Log(string.Create(CultureInfo.InvariantCulture, $"version {FirmwareVersion}, uptime {_uptime:0.0} s, led {(_led ? "on" : "off")}"));
                    break;
                default:
                    Log($"unknown command '{command}'");
                    break;
            }
        }
    }
}

/// <summary>A virtual CMSIS-DAP probe connected to one <see cref="VirtualMcu"/>.</summary>
public sealed class VirtualProbeBackend(VirtualMcu mcu, string serial = "IOTCOM-0001") : IProbeBackend
{
    /// <summary>The MCU.</summary>
    public VirtualMcu Mcu { get; } = mcu;

    /// <inheritdoc />
    public IReadOnlyList<ProbeInfo> List() => [new ProbeInfo($"c251:f001:{serial}", "Virtual CMSIS-DAP", "CMSIS-DAP", serial)];

    /// <inheritdoc />
    public IProbeSession Open(string? selector, string target, ProbeProtocol protocol, int speedKhz)
    {
        if (selector is not null && !selector.EndsWith(serial, StringComparison.OrdinalIgnoreCase) && selector != List()[0].Selector)
            throw new DeviceException($"No probe matches '{selector}'.");
        if (!string.Equals(target, Mcu.Target, StringComparison.OrdinalIgnoreCase) && !Mcu.Target.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            throw new DeviceException($"The probe sees {Mcu.Target}, not {target}: check the target name (probe-rs names such as STM32F411CEUx).");
        if (protocol == ProbeProtocol.Jtag) throw new DeviceException($"{Mcu.Target} is wired for SWD on this probe.");
        return new Session(this);
    }

    private sealed class Session(VirtualProbeBackend owner) : IProbeSession
    {
        public string Probe => "Virtual CMSIS-DAP";

        public string Target => owner.Mcu.Target;

        public byte[] Read(ulong address, int length) => owner.Mcu.Read(address, length);

        public void Write(ulong address, ReadOnlySpan<byte> data) => owner.Mcu.Write(address, data);

        public void Halt() => owner.Mcu.SetHalted(true);

        public void Run() => owner.Mcu.SetHalted(false);

        public void Reset(bool halt) => owner.Mcu.Reset(halt);

        public (CoreState State, uint Pc) Core() => owner.Mcu.Core();

        public void Flash(FirmwareImage image, Action<FlashProgress>? progress) => owner.Mcu.Flash(image, progress);

        public void EraseAll() => owner.Mcu.EraseAll();

        public void Dispose()
        {
        }
    }
}
