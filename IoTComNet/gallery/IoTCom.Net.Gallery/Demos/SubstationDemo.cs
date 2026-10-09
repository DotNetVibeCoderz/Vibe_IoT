using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Iec104;
using IoTCom.Net.Transports;
using Sim = IoTCom.Net.Protocols.Iec104.Iec104SubstationSimulator;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// A substation control room on IEC 60870-5-104: an RTU simulates a 20 kV feeder bay; a read-only SCADA connection
/// keeps the single-line diagram and the sequence of events up to date, and an operator connection (opened only when
/// control is unlocked) switches the bay with select-before-operate.
/// </summary>
public sealed partial class SubstationDemo : GalleryDemo
{
    public override string Id => "iec104-substation";
    public override Text Title => new("Substation control over IEC 104", "Kendali gardu lewat IEC 104");
    public override Text Summary => new(
        "A 20 kV feeder bay behind an RTU: watch the single-line diagram follow breaker, disconnector and earthing switch positions, switch them with select-before-operate, raise the tap changer, and trip the feeder with a simulated short circuit.",
        "Bay penyulang 20 kV di balik RTU: lihat diagram segaris mengikuti posisi pemutus, pemisah, dan saklar pentanahan, operasikan dengan select-before-operate, naikkan tap changer, dan trip penyulang dengan hubung singkat simulasi.");
    public override Text Docs => new(
        "IEC 60870-5-104 carries telecontrol between a SCADA master (controlling station) and substations, RTUs and gateways (controlled stations) over TCP port 2404. Every application data unit names a type (single point, double point, float measurement, command…), a cause of transmission (spontaneous, interrogated, activation, confirmation, termination), the station's common address and the information object addresses.\n\nThe link layer numbers every I frame; the receiver acknowledges at the latest after w = 8 frames or t2, the sender stops after k = 12 unacknowledged frames, and TESTFR keeps an idle link alive. After STARTDT the master runs a general interrogation to learn every value, then the station sends only changes, each with a CP56Time2a time tag from the RTU.\n\nCommands to switchgear use select-before-operate: the master selects the breaker, the station confirms, then the master executes; the station confirms again, operates, sends the new position as return information and terminates the activation. Interlocks refuse unsafe operations (closing onto an earthed feeder, earthing a live one).\n\nHere the RTU is IoTCom.Net's Iec104SubstationSimulator and both connections are Iec104Client. Point the same client at a real RTU with UseTcp(host).",
        "IEC 60870-5-104 membawa telekontrol antara master SCADA (controlling station) dan gardu, RTU, serta gateway (controlled station) lewat TCP port 2404. Setiap unit data aplikasi menyebut tipe (single point, double point, pengukuran float, perintah…), penyebab transmisi (spontan, interogasi, aktivasi, konfirmasi, terminasi), common address stasiun, dan alamat objek informasi.\n\nLapisan link memberi nomor setiap frame I; penerima mengakui paling lambat setelah w = 8 frame atau t2, pengirim berhenti setelah k = 12 frame belum diakui, dan TESTFR menjaga link yang diam. Setelah STARTDT master menjalankan interogasi umum untuk mengetahui semua nilai, lalu stasiun hanya mengirim perubahan, masing-masing dengan tanda waktu CP56Time2a dari RTU.\n\nPerintah ke switchgear memakai select-before-operate: master memilih pemutus, stasiun mengonfirmasi, lalu master mengeksekusi; stasiun mengonfirmasi lagi, beroperasi, mengirim posisi baru sebagai return information, dan mengakhiri aktivasi. Interlock menolak operasi yang tidak aman (menutup ke penyulang yang ditanahkan, menanahkan penyulang yang bertegangan).\n\nDi sini RTU-nya adalah Iec104SubstationSimulator milik IoTCom.Net dan kedua koneksi adalah Iec104Client. Arahkan klien yang sama ke RTU sungguhan dengan UseTcp(host).");
    public override string Category => "Energy";
    public override IReadOnlyList<string> Protocols => ["IEC 60870-5-104", "SBO", "CP56Time2a"];
    public override Difficulty Difficulty => Difficulty.Advanced;
    public override string DocsPath => "docs/en/protocols/iec104.md";

    private Sim? _rtu;
    private InMemoryTransportListener? _listener;
    private Iec104Client? _scada, _operator;
    private CancellationTokenSource? _cts;

    /// <summary>Sequence of events, newest first.</summary>
    public ObservableCollection<string> Events { get; } = [];

    [ObservableProperty] private Iec104DoublePoint _breaker = Iec104DoublePoint.On;
    [ObservableProperty] private Iec104DoublePoint _disconnector = Iec104DoublePoint.On;
    [ObservableProperty] private Iec104DoublePoint _earthing = Iec104DoublePoint.Off;
    [ObservableProperty] private bool _tripped;
    [ObservableProperty] private bool _gasLow;
    [ObservableProperty] private double _voltage;
    [ObservableProperty] private double _current;
    [ObservableProperty] private double _activePower;
    [ObservableProperty] private double _reactivePower;
    [ObservableProperty] private double _frequency;
    [ObservableProperty] private double _oilTemperature;
    [ObservableProperty] private int _tapPosition;
    [ObservableProperty] private double _energy;
    [ObservableProperty] private bool _controlUnlocked;
    [ObservableProperty] private string _device = "Q0";
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _selection = "";
    [ObservableProperty] private double _reactiveSetpoint = 1.2;

    /// <summary>Raised when the diagram should redraw.</summary>
    public event Action? Changed;

    private static uint CommandFor(string device) => device switch { "Q1" => Sim.Ioa.DisconnectorCommand, "Q8" => Sim.Ioa.EarthingCommand, _ => Sim.Ioa.BreakerCommand };

    private static string Name(uint ioa) => ioa switch
    {
        Sim.Ioa.Breaker => "Q0 circuit breaker", Sim.Ioa.Disconnector => "Q1 disconnector", Sim.Ioa.EarthingSwitch => "Q8 earthing switch",
        Sim.Ioa.ProtectionTrip => "Protection trip", Sim.Ioa.GasPressureLow => "SF6 pressure low", Sim.Ioa.TapPosition => "Tap position",
        _ => $"IOA {ioa}",
    };

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Events.Clear();
        (Selected, Selection, ControlUnlocked) = (false, "", false);
        _listener = new InMemoryTransportListener("gallery-rtu");
        _rtu = Sim.Create(o =>
        {
            o.ListenInMemory(_listener);
            o.RequireSelectBeforeOperate = true;
        });
        await _rtu.StartAsync();
        _scada = Iec104Client.Create(o => o.UseInMemory(_listener!));   // read-only by default
        _scada.AddTap(Tap);
        _scada.PointReceived += p => Ui(() => OnPoint(p));
        await _scada.ConnectAsync();
        foreach (var p in await _scada.InterrogateAsync()) OnPoint(p);
        _ = LoopAsync(_cts.Token);
        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            for (var i = 0; i < 6; i++) _rtu.Step(0.5);
            await Task.Delay(300);
            _rtu.Trip();
            await Task.Delay(200);
            ControlUnlocked = true;
            await OpenOperatorAsync();
            Device = "Q1";
            await SelectAsync(false);
        }

        SetStatus(new Text("RTU CA 1 on TCP 2404 (in-memory) · SCADA connection read-only · select-before-operate required · k = 12, w = 8.",
            "RTU CA 1 di TCP 2404 (dalam memori) · koneksi SCADA hanya-baca · wajib select-before-operate · k = 12, w = 8."));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var n = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                _rtu?.Step(0.5);
                if (n++ % 10 == 0 && _scada is { IsConnected: true } s)
                {
                    var counters = await s.CounterInterrogateAsync(ct: ct);
                    Ui(() => Energy = counters.FirstOrDefault()?.Object.Value ?? Energy);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IoTComException)
        {
        }
    }

    private void OnPoint(Iec104PointValue p)
    {
        var o = p.Object;
        switch (o.Address)
        {
            case Sim.Ioa.Breaker: Breaker = o.DoublePoint; break;
            case Sim.Ioa.Disconnector: Disconnector = o.DoublePoint; break;
            case Sim.Ioa.EarthingSwitch: Earthing = o.DoublePoint; break;
            case Sim.Ioa.ProtectionTrip: Tripped = o.IsOn; break;
            case Sim.Ioa.GasPressureLow: GasLow = o.IsOn; break;
            case Sim.Ioa.Voltage: Voltage = o.Value; break;
            case Sim.Ioa.Current: Current = o.Value; break;
            case Sim.Ioa.ActivePower: ActivePower = o.Value; break;
            case Sim.Ioa.ReactivePower: ReactivePower = o.Value; break;
            case Sim.Ioa.Frequency: Frequency = o.Value; break;
            case Sim.Ioa.OilTemperature: OilTemperature = o.Value; break;
            case Sim.Ioa.TapPosition: TapPosition = (int)o.Value; break;
        }

        if (p.Cause is Iec104Cause.Spontaneous or Iec104Cause.ReturnRemote && Iec104Types.WithoutTime(p.Type) is Iec104TypeId.SinglePoint or Iec104TypeId.DoublePoint or Iec104TypeId.StepPosition)
        {
            var state = Iec104Types.WithoutTime(p.Type) switch
            {
                Iec104TypeId.DoublePoint => o.DoublePoint switch { Iec104DoublePoint.On => "CLOSED", Iec104DoublePoint.Off => "OPEN", Iec104DoublePoint.Intermediate => "moving", _ => "FAULTY" },
                Iec104TypeId.SinglePoint => o.IsOn ? "ON" : "off",
                _ => o.Value.ToString(CultureInfo.InvariantCulture),
            };
            var stamp = o.Time?.Value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            AddEvent($"{stamp}  {Name(o.Address),-20} {state,-7} {(p.Cause == Iec104Cause.ReturnRemote ? "remote" : "spont.")}");
        }

        Changed?.Invoke();
    }

    private void AddEvent(string line)
    {
        Events.Insert(0, line);
        while (Events.Count > 16) Events.RemoveAt(Events.Count - 1);
    }

    partial void OnControlUnlockedChanged(bool value) => _ = value ? OpenOperatorAsync() : CloseOperatorAsync();

    partial void OnDeviceChanged(string value)
    {
        (Selected, Selection) = (false, "");
        Changed?.Invoke();
    }

    private Task? _opening;

    private Task OpenOperatorAsync() => _opening ??= OpenOperatorCoreAsync();

    private async Task OpenOperatorCoreAsync()
    {
        if (_listener is null || _operator is not null) return;
        var op = Iec104Client.Create(o => o.UseInMemory(_listener).AllowCommands());
        op.AddTap(Tap);
        await op.ConnectAsync();
        _operator = op;
        Ui(() => AddEvent($"{DateTime.Now:HH:mm:ss.fff}  operator connection opened (commands allowed)"));
    }

    private async Task CloseOperatorAsync()
    {
        if (_opening is { } opening) await opening;
        _opening = null;
        var op = Interlocked.Exchange(ref _operator, null);
        if (op is not null) await op.DisposeAsync();
        Ui(() => (Selected, Selection) = (false, ""));
    }

    private async Task<bool> RunAsync(string what, Func<Iec104Client, Task> action)
    {
        if (_operator is not { } op)
        {
            Ui(() => AddEvent($"{DateTime.Now:HH:mm:ss.fff}  {what}: control is locked (read-only SCADA)"));
            return false;
        }

        try
        {
            await action(op);
            return true;
        }
        catch (Exception ex) when (ex is IoTComException or InvalidOperationException)
        {
            Ui(() => AddEvent($"{DateTime.Now:HH:mm:ss.fff}  {what}: REFUSED — {(ex is DeviceException ? "interlock or station refusal" : ex.Message)}"));
            return false;
        }
    }

    /// <summary>Step 1 of SBO: select the device for open or close.</summary>
    public async Task SelectAsync(bool close)
    {
        var device = Device;
        var label = $"SELECT {device} {(close ? "CLOSE" : "OPEN")}";
        if (await RunAsync(label, op => op.CommandAsync(Iec104TypeId.DoubleCommand, new Iec104Object(CommandFor(device), close ? 2 : 1, Qualifier: 0x80))))
            Ui(() =>
            {
                (Selected, Selection) = (true, $"{device} {(close ? "CLOSE" : "OPEN")}");
                AddEvent($"{DateTime.Now:HH:mm:ss.fff}  {label}: confirmed — execute within 10 s");
                Changed?.Invoke();
            });
    }

    /// <summary>Step 2 of SBO: execute the selected operation.</summary>
    public async Task ExecuteAsync()
    {
        if (!Selected) return;
        var close = Selection.EndsWith("CLOSE", StringComparison.Ordinal);
        var device = Selection[..2];
        Ui(() => (Selected, Selection) = (false, ""));
        await RunAsync($"EXECUTE {device}", op => op.CommandAsync(Iec104TypeId.DoubleCommand, new Iec104Object(CommandFor(device), close ? 2 : 1), waitForTermination: true));
        Changed?.Invoke();
    }

    /// <summary>Resets the latched protection trip.</summary>
    public Task ResetProtectionAsync() => RunAsync("RESET PROTECTION", op => op.SingleCommandAsync(Sim.Ioa.TripReset, true, selectBeforeOperate: true));

    /// <summary>One tap step higher or lower.</summary>
    public Task StepTapAsync(bool higher) => RunAsync($"TAP {(higher ? "RAISE" : "LOWER")}", op => op.RegulatingStepAsync(Sim.Ioa.TapCommand, higher, selectBeforeOperate: true));

    /// <summary>Sends the reactive power set point.</summary>
    public Task ApplySetpointAsync() => RunAsync($"Q SET POINT {ReactiveSetpoint:0.0} Mvar", op => op.SetpointAsync(Sim.Ioa.ReactiveSetpoint, Math.Round(ReactiveSetpoint, 1), selectBeforeOperate: true));

    /// <summary>A short circuit on the feeder.</summary>
    public void ShortCircuit() => _rtu?.Trip();

    /// <summary>Toggles the SF6 pressure alarm.</summary>
    public void ToggleGas() => _rtu?.SetGasPressureLow(!GasLow);

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        await CloseOperatorAsync();
        if (_scada is not null) await _scada.DisposeAsync();
        if (_rtu is not null) await _rtu.DisposeAsync();
        (_scada, _rtu, _listener) = (null, null, null);
        _cts?.Dispose();
        _cts = null;
        ControlUnlocked = false;
        Status = "";
    }
}
