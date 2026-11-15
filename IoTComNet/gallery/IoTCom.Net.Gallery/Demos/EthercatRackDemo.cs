using System.Buffers.Binary;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.EtherCat;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// An EtherCAT I/O rack on a conveyor: the master scans the line, walks it to SAFE-OP and exchanges the process image
/// with one LRW frame every 20 ms. Behind a write lock the operator brings the line to OP and switches outputs; the plant
/// side has an emergency stop, a guard door, a cable that can be cut after any terminal and a lossy link.
/// </summary>
public sealed partial class EthercatRackDemo : GalleryDemo
{
    public override string Id => "ethercat-rack";
    public override Text Title => new("I/O rack over EtherCAT", "Rak I/O lewat EtherCAT");
    public override Text Summary => new(
        "An EtherCAT master drives a conveyor's I/O terminals with one frame every 20 ms that passes through every terminal and back; walk the state machine to OP behind a write lock, switch outputs, press the emergency stop, or cut the cable and watch the working counter fall short.",
        "Master EtherCAT mengendalikan terminal I/O sebuah konveyor dengan satu frame setiap 20 ms yang melewati setiap terminal lalu kembali; jalankan state machine sampai OP di balik kunci tulis, nyalakan output, tekan tombol darurat, atau putus kabelnya dan lihat working counter berkurang.");
    public override Text Docs => new(
        "EtherCAT is Ethernet with a twist: the master sends ordinary frames (EtherType 0x88A4), and every slave reads and writes its part of a frame while it passes through, in hardware, before forwarding it. The last slave sends the frame back along the same cable, so a single frame of a few dozen bytes services the whole line in microseconds. Each datagram in the frame ends with a working counter (WKC): every slave that served it adds 1 for a read and 2 for a write. The master knows what to expect; a short count means a slave did not see the frame — a broken cable, a slave that left OP, a lost frame.\n\nBefore any of that, the master scans: a broadcast read counts the slaves (the WKC is the count), positional writes give each one a station address (0x1001, 0x1002…), and the SII EEPROM tells vendor, product, name, mailbox and process-data sizes. Then the AL state machine: INIT, PRE-OP (the mailbox works: CoE objects such as 0x1008 device name), SAFE-OP (SyncManagers and FMMUs map every terminal into one logical process image; inputs are valid, outputs held safe), OP (outputs drive the machine). Here the master is IoTCom's EthercatMaster and the rack is EthercatRackSimulator on a VirtualEthercatSegment; on real hardware the same master runs over a dedicated network port (AF_PACKET or Npcap). Cycle timing on a desktop OS is best effort, not hard real-time.",
        "EtherCAT adalah Ethernet dengan satu keunikan: master mengirim frame biasa (EtherType 0x88A4), dan setiap slave membaca dan menulis bagiannya dari frame saat frame itu lewat, di perangkat keras, sebelum meneruskannya. Slave terakhir mengirim frame kembali lewat kabel yang sama, sehingga satu frame berukuran beberapa puluh byte melayani seluruh jalur dalam hitungan mikrodetik. Setiap datagram di dalam frame diakhiri working counter (WKC): setiap slave yang melayaninya menambah 1 untuk baca dan 2 untuk tulis. Master tahu berapa yang diharapkan; hitungan yang kurang berarti ada slave yang tidak melihat frame — kabel putus, slave yang keluar dari OP, frame yang hilang.\n\nSebelum itu, master memindai: baca broadcast menghitung slave (WKC adalah jumlahnya), tulis posisional memberi setiap slave alamat stasiun (0x1001, 0x1002…), dan EEPROM SII memberi tahu vendor, produk, nama, mailbox, dan ukuran process data. Lalu state machine AL: INIT, PRE-OP (mailbox berfungsi: objek CoE seperti nama perangkat 0x1008), SAFE-OP (SyncManager dan FMMU memetakan setiap terminal ke satu process image logis; input valid, output ditahan aman), OP (output menggerakkan mesin). Di sini master-nya adalah EthercatMaster milik IoTCom dan raknya adalah EthercatRackSimulator di VirtualEthercatSegment; di perangkat keras sungguhan master yang sama berjalan di port jaringan khusus (AF_PACKET atau Npcap). Waktu siklus di OS desktop bersifat best effort, bukan hard real-time.");
    public override string Category => "Industrial";
    public override IReadOnlyList<string> Protocols => ["EtherCAT", "CoE", "LRW", "EtherType 0x88A4"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/ethercat.md";

    /// <summary>Output channels of the DO8 terminal.</summary>
    public static readonly (int Bit, Text Name)[] OutputChannels =
    [
        (0, new("Conveyor motor", "Motor konveyor")), (1, new("Horn", "Klakson")), (2, new("Green lamp", "Lampu hijau")), (3, new("Red lamp", "Lampu merah")),
    ];

    private EthercatRackSimulator? _rack;
    private EthercatMaster? _master;
    private CancellationTokenSource? _cts;
    private volatile bool _busy;

    internal EthercatRackSimulator? Rack => _rack;
    internal EthercatMaster? Master => _master;

    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private string _lineState = "INIT";
    [ObservableProperty] private string _counters = "";
    [ObservableProperty] private bool _wkcOk = true;
    [ObservableProperty] private bool _emergencyStop;
    [ObservableProperty] private bool _guardOpen;
    [ObservableProperty] private bool _cableCut;
    [ObservableProperty] private bool _lossy;
    [ObservableProperty] private string _coe = "";
    [ObservableProperty] private byte _outputBits;

    /// <summary>Raised after each UI refresh (the rail and the conveyor redraw).</summary>
    public event Action? Refreshed;

    /// <summary>Output byte as commanded.</summary>
    public byte CommandedOutputs => OutputBits;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        OutputBits = 0;
        _rack = new EthercatRackSimulator();
        _master = EthercatMaster.Create(o => o.UseLink(_rack.Segment).AllowWrites());   // the demo's write lock sits in front of OP and outputs
        _master.AddTap(Tap);
        await _master.ConnectAsync();
        var slaves = await _master.ScanAsync();
        AddLog($"BRD 0x0000 → wkc {slaves.Count}: {slaves.Count} slaves; APWR station addresses 0x1001–0x{0x1000 + slaves.Count:X4}; SII read");
        foreach (var s in slaves) AddLog($"  #{s.Position} {s.Name} · vendor 0x{s.Vendor:X8} product 0x{s.Product:X8} · in {s.InputBytes} B out {s.OutputBytes} B{(s.HasCoe ? " · CoE" : "")}");
        await RequestAsync(EcatState.SafeOp);
        _ = LoopAsync(_cts.Token);

        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            await RequestAsync(EcatState.Op);
            SetOutput(0, true);
            SetOutput(2, true);
            for (var i = 0; i < 250 && _master is not null; i++)
            {
                _rack.Step(0.02);
                await _master.ExchangeAsync();
            }

            await ReadNameAsync();
            Refresh();
        }

        SetStatus(new Text("EtherCAT master EthercatMaster on a VirtualEthercatSegment · Sim EK coupler, DI8, DO8, AI4 · one LRW frame every 20 ms · SII identity, CoE mailbox, FMMU process image.",
            "Master EtherCAT EthercatMaster di VirtualEthercatSegment · coupler Sim EK, DI8, DO8, AI4 · satu frame LRW setiap 20 ms · identitas SII, mailbox CoE, process image FMMU."));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var n = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(20, ct);
                if (_busy || _master is null || _rack is null) continue;
                _rack.Step(0.02);
                try
                {
                    await _master.ExchangeAsync(ct);
                }
                catch (InvalidOperationException)
                {
                    continue;   // below SAFE-OP there is no process image
                }

                if (++n % 5 == 0) Ui(Refresh);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IoTComException ex)
        {
            AddLog($"cycle: {ex.Message}");
        }
    }

    private void Refresh()
    {
        if (_master is null) return;
        var st = _master.Statistics;
        WkcOk = st.LastWorkingCounter == st.ExpectedWorkingCounter;
        Counters = $"wkc {st.LastWorkingCounter}/{st.ExpectedWorkingCounter} · {st.Cycles} cycles · {st.WorkingCounterErrors} short · {st.LostFrames} lost · rtt {st.LastRoundTrip.TotalMilliseconds:0.000} ms";
        var states = _master.Slaves.Select(s => s.State).Distinct().ToList();
        LineState = states.Count == 1 ? EcatAlStatus.Name(states[0]) : string.Join(" / ", _master.Slaves.Select(s => EcatAlStatus.Name(s.State)));
        Refreshed?.Invoke();
    }

    /// <summary>Inputs of a slave from the last exchange (empty before SAFE-OP).</summary>
    internal byte[] InputsOf(int position) => _master is { } m && m.Image.Length > 0 && position < m.Slaves.Count ? m.InputsOf(m.Slaves[position]).ToArray() : [];

    /// <summary>Analog channel in engineering units (0.01 per count).</summary>
    internal double Analog(int channel)
    {
        var b = InputsOf(3);
        return b.Length >= 8 ? BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(channel * 2)) / 100.0 : 0;
    }

    public async Task RequestAsync(EcatState state)
    {
        if (_master is null) return;
        if (state == EcatState.Op && !WritesAllowed)
        {
            AddLog("OP: refused — the write lock is closed; SAFE-OP keeps the outputs safe while inputs stay live");
            return;
        }

        _busy = true;
        try
        {
            await _master.RequestStateAsync(state);
            await _master.RefreshStatesAsync();
            AddLog(state switch
            {
                EcatState.PreOp => "PRE-OP: mailbox SyncManagers 0/1 configured; CoE objects readable",
                EcatState.SafeOp => $"SAFE-OP: FMMUs map a {_master.Image.Length}-byte process image (outputs first, then inputs); expected wkc {_master.Statistics.ExpectedWorkingCounter}",
                EcatState.Op => "OP: valid outputs sent first, then every terminal switched to OP",
                _ => $"{EcatAlStatus.Name(state)} requested",
            });
        }
        catch (Exception ex) when (ex is IoTComException or NotSupportedException)
        {
            AddLog($"{EcatAlStatus.Name(state)}: {ex.Message}");
            await _master.RefreshStatesAsync();
        }
        finally
        {
            _busy = false;
            Ui(Refresh);
        }
    }

    public async Task AcknowledgeAsync()
    {
        if (_master is null) return;
        _busy = true;
        try
        {
            foreach (var s in _master.Slaves.Where(s => s.StatusCode != 0).ToList())
            {
                var target = WritesAllowed ? EcatState.Op : EcatState.SafeOp;
                await _master.AcknowledgeErrorAsync(s, target);
                AddLog($"{s.Name}: error acknowledged, back in {EcatAlStatus.Name(target)}");
            }
        }
        catch (IoTComException ex)
        {
            AddLog($"acknowledge: {ex.Message}");
        }
        finally
        {
            _busy = false;
            Ui(Refresh);
        }
    }

    public void SetOutput(int bit, bool on)
    {
        if (_master is null) return;
        if (!WritesAllowed)
        {
            AddLog($"output {bit}: refused — the write lock is closed (read-only master)");
            return;
        }

        var next = on ? (byte)(OutputBits | (1 << bit)) : (byte)(OutputBits & ~(1 << bit));
        if (next == OutputBits) return;
        OutputBits = next;
        if (_master.Image.Length == 0) return;
        _master.SetOutputs(_master.Slaves[2], [next]);
        if (_master.Slaves[2].State != EcatState.Op) AddLog($"output {bit} {(on ? "on" : "off")} is in the image, but DO8 holds its outputs safe until OP");
    }

    partial void OnWritesAllowedChanged(bool value)
    {
        if (value || _master is null) return;
        OutputBits = 0;
        if (_master.Image.Length > 0) _master.SetOutputs(_master.Slaves[2], [0]);
        if (_master.Slaves.Any(s => s.State == EcatState.Op)) _ = RequestAsync(EcatState.SafeOp);
    }

    partial void OnEmergencyStopChanged(bool value)
    {
        _rack?.PressEmergencyStop(value);
        AddLog(value ? "emergency stop pressed: DI2 drops, the motor contactor opens in the plant" : "emergency stop released");
    }

    partial void OnGuardOpenChanged(bool value)
    {
        if (_rack is not null) _rack.GuardClosed = !value;
        AddLog(value ? "guard door opened: DI3 drops" : "guard door closed");
    }

    partial void OnCableCutChanged(bool value)
    {
        if (_rack is null) return;
        _rack.Segment.BreakAfter = value ? 2 : null;
        AddLog(value ? "cable cut after DI8: the frame turns back there; DO8 and AI4 no longer count — wkc falls short" : "cable reconnected");
    }

    partial void OnLossyChanged(bool value)
    {
        if (_rack is not null) _rack.Segment.LossRate = value ? 0.2 : 0;
        AddLog(value ? "20 % of frames lost: lost cycles are counted; inputs keep their last value" : "link clean again");
    }

    public async Task ReadNameAsync()
    {
        if (_master is null) return;
        _busy = true;
        try
        {
            var ai = _master.Slaves[3];
            var name = System.Text.Encoding.ASCII.GetString(await _master.SdoReadAsync(ai, 0x1008, 0));
            var filter = BinaryPrimitives.ReadUInt16LittleEndian(await _master.SdoReadAsync(ai, 0x8000, 0x15));
            Ui(() => Coe = $"0x1008 \"{name}\" · 0x8000:15 filter {filter} ({(filter switch { 0 => "50 Hz", 1 => "60 Hz", 2 => "100 Hz", _ => "500 Hz" })})");
            AddLog($"CoE upload 0x1008:00 → \"{name}\"; 0x8000:15 → {filter}");
        }
        catch (IoTComException ex)
        {
            AddLog($"CoE: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task CycleFilterAsync()
    {
        if (_master is null) return;
        if (!WritesAllowed)
        {
            AddLog("CoE download 0x8000:15: refused — the write lock is closed");
            return;
        }

        _busy = true;
        try
        {
            var ai = _master.Slaves[3];
            var next = (ushort)((BinaryPrimitives.ReadUInt16LittleEndian(await _master.SdoReadAsync(ai, 0x8000, 0x15)) + 1) % 4);
            await _master.SdoWriteAsync(ai, 0x8000, 0x15, BitConverter.GetBytes(next));
            AddLog($"CoE download 0x8000:15 = {next} → success");
        }
        catch (IoTComException ex)
        {
            AddLog($"CoE: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }

        await ReadNameAsync();
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_master is not null) await _master.DisposeAsync();
        (_master, _rack) = (null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        (EmergencyStop, GuardOpen, CableCut, Lossy) = (false, false, false, false);
        (Counters, Coe, LineState, Status) = ("", "", "INIT", "");
    }
}
