using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Debug;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Transport.Usb;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Design use case 12: probe and flash firmware. A dev board (simulated STM32F411) on the bench, reached through a SWD
/// probe or — with the BOOT0 jumper set — its USB DFU bootloader. Read memory, flash firmware versions page by page,
/// stream RTT logs and send commands back, halt and reset; everything that changes the board sits behind a write lock.
/// </summary>
public sealed partial class ProbeFlashDemo : GalleryDemo
{
    public override string Id => "probe-flash";
    public override Text Title => new("Probe and flash firmware", "Probe dan flash firmware");
    public override Text Summary => new(
        "A dev board on the bench through a SWD probe or its USB DFU bootloader: read memory, flash firmware page by page and watch it boot, stream RTT logs and switch the board's LED over RTT, halt and reset — every change behind a write lock.",
        "Board pengembangan di meja kerja lewat probe SWD atau bootloader USB DFU-nya: baca memori, flash firmware halaman demi halaman dan lihat ia booting, alirkan log RTT dan nyalakan LED board lewat RTT, halt dan reset — setiap perubahan di balik kunci tulis.");
    public override Text Docs => new(
        "A debug probe talks to a microcontroller's debug port over SWD (two wires: SWDIO and SWCLK) or JTAG. Through it the host reads and writes memory while the core runs, halts and resumes the core, and programs flash with a small flash algorithm loaded into the chip's RAM: erase the sectors the image needs, program, verify, reset. IoTCom's DebugProbe wraps probe-rs, which knows the flash layout and algorithms of hundreds of chips and speaks CMSIS-DAP, ST-Link, J-Link, ESP USB-JTAG, FTDI and WCH-Link.\n\nRTT (SEGGER Real-Time Transfer) is how firmware logs without a UART: it keeps a control block in RAM — the ID \"SEGGER RTT\", then ring buffers for target-to-host (up) and host-to-target (down) channels. The host finds the block by scanning RAM, reads new bytes between the read and write offsets, and moves the read offset; the core never stops. Here the down channel carries commands: led on, led off, status.\n\nDFU (USB Device Firmware Upgrade) needs no probe: a bootloader enumerates as a USB device with a DFU interface. ST's DfuSe flavour adds commands inside DNLOAD block 0 — 0x41 erase a page, 0x21 set the address — and publishes the memory layout in the interface string (@Internal Flash /0x08000000/128*002Kg). After the last block, a zero-length download makes the device leave DFU mode and start the new firmware. The board here is VirtualMcu, the probe VirtualProbeBackend and the bootloader VirtualDfuDevice; with real hardware the same code runs against probe-rs and the USB stack.",
        "Probe debug berbicara dengan port debug mikrokontroler lewat SWD (dua kabel: SWDIO dan SWCLK) atau JTAG. Lewat probe, host membaca dan menulis memori saat core berjalan, menghentikan dan melanjutkan core, serta memprogram flash dengan algoritma flash kecil yang dimuat ke RAM chip: hapus sektor yang dibutuhkan image, program, verifikasi, reset. DebugProbe milik IoTCom membungkus probe-rs, yang mengenal tata letak flash dan algoritma ratusan chip serta berbicara CMSIS-DAP, ST-Link, J-Link, ESP USB-JTAG, FTDI, dan WCH-Link.\n\nRTT (SEGGER Real-Time Transfer) adalah cara firmware mencatat log tanpa UART: ia menyimpan control block di RAM — ID \"SEGGER RTT\", lalu ring buffer untuk kanal target-ke-host (up) dan host-ke-target (down). Host menemukan blok dengan memindai RAM, membaca byte baru di antara offset baca dan tulis, lalu memajukan offset baca; core tidak pernah berhenti. Di sini kanal down membawa perintah: led on, led off, status.\n\nDFU (USB Device Firmware Upgrade) tidak butuh probe: bootloader muncul sebagai perangkat USB dengan antarmuka DFU. Varian DfuSe dari ST menambahkan perintah di dalam blok DNLOAD 0 — 0x41 hapus halaman, 0x21 atur alamat — dan menerbitkan tata letak memori di string antarmuka (@Internal Flash /0x08000000/128*002Kg). Setelah blok terakhir, download dengan panjang nol membuat perangkat keluar dari mode DFU dan menjalankan firmware baru. Board di sini adalah VirtualMcu, probe-nya VirtualProbeBackend, dan bootloader-nya VirtualDfuDevice; dengan perangkat keras sungguhan kode yang sama berjalan dengan probe-rs dan stack USB.");
    public override string Category => "Workbench";
    public override IReadOnlyList<string> Protocols => ["SWD", "RTT", "USB DFU", "DfuSe"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/debug.md";

    /// <summary>Firmware versions the demo can flash.</summary>
    public static readonly string[] Versions = ["1.0.0", "1.1.0", "2.0.0"];

    private VirtualMcu? _mcu;
    private DebugProbe? _probe;
    private VirtualUsbBus? _bus;
    private VirtualDfuDevice? _dfuDevice;
    private RttClient? _rtt;
    private CancellationTokenSource? _cts;
    private volatile bool _busy;
    private readonly int[] _pageVersion = new int[VirtualMcu.FlashSize / VirtualMcu.PageSize];   // -1 erased, else index into Versions

    internal VirtualMcu? Mcu => _mcu;

    /// <summary>Version index per flash page (-1 = erased).</summary>
    internal IReadOnlyList<int> PageVersions => _pageVersion;

    /// <summary>RTT terminal lines.</summary>
    public ObservableCollection<string> Terminal { get; } = [];

    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private bool _dfuMode;
    [ObservableProperty] private int _versionIndex = 1;
    [ObservableProperty] private string _core = "";
    [ObservableProperty] private string _firmware = "";
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _memoryAddress = "0x08000200";
    [ObservableProperty] private string _memory = "";
    [ObservableProperty] private string _command = "led on";
    [ObservableProperty] private bool _probeActive;

    /// <summary>Raised when the board view should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Terminal.Clear();
        _mcu = new VirtualMcu(firmware: VirtualMcu.BuildDemoFirmware(Versions[0]));
        MarkPages(VirtualMcu.BuildDemoFirmware(Versions[0]), 0);
        _probe = DebugProbe.Create(o => o.UseVirtual(new VirtualProbeBackend(_mcu)).UseTarget("STM32F411CEUx").AllowWrites());   // the demo's write lock sits in front
        _probe.AddTap(Tap);
        await _probe.ConnectAsync();
        _bus = new VirtualUsbBus();
        _dfuDevice = new VirtualDfuDevice(_mcu);
        AddLog($"Attached to {_probe.Target} through {_probe.ProbeName} (SWD, 4 MHz)");
        _mcu.Step(0.2);
        await AttachRttAsync();
        await ReadMemoryAsync();
        _ = LoopAsync(_cts.Token);

        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            VersionIndex = 2;
            await FlashAsync();
            for (var i = 0; i < 4; i++)
            {
                _mcu.Step(0.5);
                await PollRttAsync();
            }

            Command = "led on";
            await SendAsync();
            _mcu.Step(0.3);
            await PollRttAsync();
            await ReadMemoryAsync();
        }

        SetStatus(new Text("Simulated STM32F411CEUx (256 KiB flash in 2 KiB pages, 128 KiB RAM) · virtual CMSIS-DAP probe over SWD · RTT control block at 0x20000400 · DfuSe bootloader 0483:df11 when BOOT0 is set.",
            "STM32F411CEUx simulasi (flash 256 KiB dalam halaman 2 KiB, RAM 128 KiB) · probe CMSIS-DAP virtual lewat SWD · control block RTT di 0x20000400 · bootloader DfuSe 0483:df11 saat BOOT0 diset."));
    }

    private void MarkPages(FirmwareImage image, int version)
    {
        foreach (var s in image.Segments)
        {
            var first = (int)((s.Address - VirtualMcu.FlashBase) / VirtualMcu.PageSize);
            var last = (int)((s.End - 1 - VirtualMcu.FlashBase) / VirtualMcu.PageSize);
            for (var p = first; p <= last; p++) _pageVersion[p] = version;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(200, ct);
                if (_busy || _mcu is null) continue;
                if (!DfuMode) _mcu.Step(0.2);
                await PollRttAsync();
                await RefreshCoreAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AttachRttAsync()
    {
        if (_probe is null) return;
        try
        {
            _rtt = await _probe.AttachRttAsync(VirtualMcu.RamBase, VirtualMcu.RamSize);
            AddLog($"RTT control block at 0x{_rtt.Address:X8}: up '{_rtt.UpChannels[0].Name}' {_rtt.UpChannels[0].Size} B, down '{_rtt.DownChannels[0].Name}' {_rtt.DownChannels[0].Size} B");
        }
        catch (DeviceException ex)
        {
            _rtt = null;
            AddLog($"RTT: {ex.Message}");
        }
    }

    private async Task PollRttAsync()
    {
        if (_probe is null || DfuMode) return;
        if (_rtt is null)
        {
            if (_mcu?.HasValidFirmware == true && _mcu.LinesLogged > 0) await AttachRttAsync();
            if (_rtt is null) return;
        }

        try
        {
            var text = await _rtt.ReadTextAsync();
            if (text.Length == 0) return;
            Ui(() =>
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Terminal.Add(line);
                while (Terminal.Count > 200) Terminal.RemoveAt(0);
                Changed?.Invoke();
            });
        }
        catch (Exception ex) when (ex is IoTComException)
        {
            _rtt = null;
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (_probe is null || _mcu is null) return;
        var (state, pc) = DfuMode ? (CoreState.Running, 0x1FFF0000u) : await _probe.CoreAsync();
        Ui(() =>
        {
            Core = DfuMode ? "system bootloader (DFU mode)" : $"{state}{(state == CoreState.LockedUp ? " — no valid vector table" : $" · PC 0x{pc:X8}")}";
            Firmware = _mcu.FirmwareVersion is { } v ? $"firmware {v} · flashed {_mcu.FlashCycles}×" : "flash erased";
            Changed?.Invoke();
        });
    }

    private bool Locked(string what)
    {
        if (WritesAllowed) return false;
        AddLog($"{what}: refused — the write lock is closed (read-only probe)");
        return true;
    }

    private IProgress<FlashProgress> Reporter() => new Progress<FlashProgress>(p => Ui(() =>
    {
        Phase = p.Phase;
        Progress = p.Fraction;
        ProbeActive = !ProbeActive;
        Changed?.Invoke();
    }));

    public async Task FlashAsync()
    {
        if (_probe is null || _mcu is null || Locked("flash")) return;
        var version = VersionIndex;
        var image = VirtualMcu.BuildDemoFirmware(Versions[version]);
        _busy = true;
        try
        {
            if (DfuMode)
            {
                await using var dfu = DfuClient.Create(o => o.UseVirtual(_bus!).AllowWrites());
                await dfu.ConnectAsync();
                dfu.Usb!.AddTap(Tap);
                await dfu.DownloadAsync(image, Reporter());
                AddLog($"DfuSe: {_dfuDevice!.PagesErased} pages erased (0x41), {_dfuDevice.BlocksWritten} blocks written; firmware {Versions[version]} waits for 'Leave DFU'");
            }
            else
            {
                await _probe.FlashAsync(image, Reporter());
                Terminal.Clear();
                _rtt = null;
                AddLog($"SWD: erased, programmed and verified {image} — reset into firmware {Versions[version]}");
            }

            Array.Fill(_pageVersion, -1);
            MarkPages(image, version);
        }
        catch (IoTComException ex)
        {
            AddLog($"flash: {ex.Message}");
        }
        finally
        {
            _busy = false;
            Ui(() => Phase = "");
            await RefreshCoreAsync();
            await ReadMemoryAsync();
        }
    }

    public async Task EraseAsync()
    {
        if (_probe is null || DfuMode || Locked("erase all")) return;
        await _probe.EraseAllAsync();
        await _probe.ResetAsync();
        Array.Fill(_pageVersion, -1);
        _rtt = null;
        AddLog("SWD: mass erase, reset — the core locks up without a vector table");
        await RefreshCoreAsync();
        await ReadMemoryAsync();
    }

    public async Task ControlAsync(string action)
    {
        if (_probe is null || DfuMode || Locked(action)) return;
        switch (action)
        {
            case "halt": await _probe.HaltAsync(); break;
            case "run": await _probe.RunAsync(); break;
            case "reset":
                await _probe.ResetAsync();
                Terminal.Clear();
                _rtt = null;
                break;
        }

        AddLog($"SWD: {action}");
        await RefreshCoreAsync();
    }

    public async Task SendAsync()
    {
        if (_rtt is null || DfuMode || Locked($"RTT '{Command}'")) return;
        var n = await _rtt.WriteAsync(Encoding.ASCII.GetBytes(Command + "\n"));
        AddLog($"RTT down: '{Command}' ({n} bytes)");
    }

    public async Task ReadMemoryAsync()
    {
        if (_probe is null) return;
        try
        {
            var text = MemoryAddress.Trim();
            var address = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(text[2..], 16) : ulong.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            var data = await _probe.ReadMemoryAsync(address, 64);
            var sb = new StringBuilder();
            for (var i = 0; i < data.Length; i += 16)
            {
                var line = data.AsSpan(i, 16);
                sb.Append($"{address + (ulong)i:X8}  {HexDump.ToHex(line)}  ");
                foreach (var b in line) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                sb.Append('\n');
            }

            Ui(() => Memory = sb.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is IoTComException or FormatException or OverflowException)
        {
            Ui(() => Memory = ex.Message);
        }
    }

    partial void OnDfuModeChanged(bool value)
    {
        if (_bus is null || _dfuDevice is null || _mcu is null) return;
        if (value)
        {
            _bus.Add(_dfuDevice);
            _rtt = null;
            AddLog("BOOT0 set, reset: the chip starts its system bootloader and enumerates as 0483:df11 \"DFU in FS Mode\" (@Internal Flash /0x08000000/128*002Kg)");
        }
        else
        {
            _bus.Remove(_dfuDevice.Info.Id);
            _mcu.Reset();
            Terminal.Clear();
            AddLog("BOOT0 cleared, reset: the application starts from flash");
        }

        Changed?.Invoke();
    }

    public async Task LeaveDfuAsync()
    {
        if (!DfuMode || Locked("leave DFU")) return;
        await using var dfu = DfuClient.Create(o => o.UseVirtual(_bus!).AllowWrites());
        await dfu.LeaveAsync();
        AddLog("DfuSe leave: zero-length DNLOAD → manifest; the device starts the application");
        DfuMode = false;
        await RefreshCoreAsync();
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_probe is not null) await _probe.DisposeAsync();
        (_probe, _mcu, _rtt, _bus, _dfuDevice) = (null, null, null, null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        DfuMode = false;
        (Core, Firmware, Memory, Phase, Status) = ("", "", "", "", "");
    }
}
