using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.IsoTp;
using IoTCom.Net.Protocols.Uds;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Vehicle diagnostics: an engine ECU simulator and a scan tool on a virtual CAN bus. The tester reads live data
/// with OBD-II (mode 01), identification and DTCs with UDS, and — only when writes are allowed — clears DTCs and
/// writes a workshop code after security access. ISO-TP segmentation runs in the Rust state machine.
/// </summary>
public sealed partial class VehicleDiagnosticsDemo : GalleryDemo
{
    public override string Id => "can-uds";
    public override Text Title => new("Vehicle diagnostics", "Diagnostik kendaraan");
    public override Text Summary => new(
        "A scan tool talks to a simulated engine ECU over CAN: live OBD-II data, VIN and part numbers over UDS, trouble codes with the MIL lamp, security access and a guarded write.",
        "Scan tool berbicara dengan ECU mesin simulasi lewat CAN: data langsung OBD-II, VIN dan nomor part lewat UDS, kode kerusakan dengan lampu MIL, security access, dan penulisan yang dijaga.");
    public override Text Docs => new(
        "Cars expose diagnostics on the CAN bus. OBD-II (SAE J1979) is the legislated part: functional requests on 0x7DF, answers from 0x7E8, live values (mode 01), stored trouble codes (03), pending codes (07), clear (04) and the VIN (09). UDS (ISO 14229) is the full diagnostic protocol behind it: sessions, security access with seed and key, data identifiers (F190 = VIN), DTCs with status bits and routines.\n\nMessages longer than one CAN frame — the 17-character VIN, a DTC list — are split by ISO-TP (ISO 15765-2) into a first frame, flow control and consecutive frames. IoTCom.Net runs ISO-TP in a fuzzed Rust state machine (iotcom_isotp) and drives it from C#.\n\nThe tester is read-only by default: clearing codes or writing data throws ReadOnlyModeException until you allow writes. On a real car use an slcan adapter (CANable) or SocketCAN — same code, different CanBus URI.",
        "Mobil membuka diagnostik di bus CAN. OBD-II (SAE J1979) adalah bagian yang diwajibkan regulasi: request fungsional di 0x7DF, jawaban dari 0x7E8, nilai langsung (mode 01), kode kerusakan tersimpan (03), kode tertunda (07), hapus (04), dan VIN (09). UDS (ISO 14229) adalah protokol diagnostik lengkap di baliknya: session, security access dengan seed dan key, data identifier (F190 = VIN), DTC dengan bit status, dan routine.\n\nPesan yang lebih panjang dari satu frame CAN — VIN 17 karakter, daftar DTC — dipecah oleh ISO-TP (ISO 15765-2) menjadi first frame, flow control, dan consecutive frame. IoTCom.Net menjalankan ISO-TP di state machine Rust yang di-fuzz (iotcom_isotp) dan menggerakkannya dari C#.\n\nTester bersifat read-only secara bawaan: menghapus kode atau menulis data melempar ReadOnlyModeException sampai penulisan diizinkan. Di mobil sungguhan pakai adapter slcan (CANable) atau SocketCAN — kode sama, hanya URI CanBus yang berbeda.");
    public override string Category => "Automotive";
    public override IReadOnlyList<string> Protocols => ["CAN", "ISO-TP (Rust)", "UDS", "OBD-II"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/uds.md";

    private VirtualCanNetwork? _network;
    private EcuSimulator? _ecu;
    private VirtualCanBus? _tester;
    private ObdClient? _obd;
    private UdsClient? _uds;
    private CancellationTokenSource? _poll;
    private IDisposable[] _taps = [];

    public bool NativeAvailable { get; } = IsoTpMachine.IsSupported;

    public TrendBuffer RpmTrend { get; } = new(160, 500);
    public TrendBuffer SpeedTrend { get; } = new(160, 20);
    public TrendBuffer CoolantTrend { get; } = new(160, 5);

    public ObservableCollection<DtcRow> Dtcs { get; } = [];

    [ObservableProperty] private double _rpm;
    [ObservableProperty] private double _speed;
    [ObservableProperty] private double _coolant;
    [ObservableProperty] private double _throttle;
    [ObservableProperty] private double _load;
    [ObservableProperty] private double _voltage;
    [ObservableProperty] private double _fuel;
    [ObservableProperty] private double _maf;
    [ObservableProperty] private string _vin = "—";
    [ObservableProperty] private string _partNumber = "—";
    [ObservableProperty] private string _softwareVersion = "—";
    [ObservableProperty] private string _session = "Default";
    [ObservableProperty] private string _workshopCode = "—";
    [ObservableProperty] private bool _mil;
    [ObservableProperty] private bool _unlocked;
    [ObservableProperty] private bool _allowWrites;
    [ObservableProperty] private bool _overheating;
    [ObservableProperty] private long _canFrames;

    partial void OnAllowWritesChanged(bool value)
    {
        if (_uds is not null) _uds.Options.ReadOnly = !value;
    }

    protected override async Task OnStartAsync()
    {
        if (!NativeAvailable) throw new PlatformNotSupportedException(Loc.L("The iotcom_isotp native library is not available on this platform.", "Pustaka native iotcom_isotp tidak tersedia di platform ini."));

        // 1) A private virtual CAN bus with the engine ECU simulator on it (0x7E0 → 0x7E8, functional 0x7DF).
        _network = new VirtualCanNetwork("vehicle");
        _ecu = EcuSimulator.Create(_network.CreateNode(), o => o.TimeScale = 1);
        _ecu.Vehicle.WarmUp();
        await _ecu.StartAsync();

        // 2) The scan tool: one CAN node shared by an OBD-II client and a UDS client (read-only unless allowed).
        _tester = _network.CreateNode(o => o.Name = "scan tool");
        await _tester.ConnectAsync();
        _obd = ObdClient.Create(_tester, o => o.ReadOnly = !AllowWrites);
        _uds = UdsClient.Create(_tester, o => o.ReadOnly = !AllowWrites);
        _taps = [_tester.AddTap(Tap), _obd.AddTap(Tap), _uds.AddTap(Tap)];
        await _obd.ConnectAsync();
        await _uds.ConnectAsync();

        // 3) Identification over UDS (ReadDataByIdentifier; the VIN needs ISO-TP multi-frame).
        var vin = await _uds.ReadVinAsync();
        var part = await _uds.ReadStringAsync(UdsDid.SparePartNumber);
        var sw = await _uds.ReadStringAsync(UdsDid.SoftwareVersion);
        var code = await _uds.ReadStringAsync(UdsDid.RepairShopCode);
        Ui(() => (Vin, PartNumber, SoftwareVersion, WorkshopCode) = (vin, part, sw, code));
        await ReadDtcsAsync();

        _poll = new CancellationTokenSource();
        _ = PollAsync(_poll.Token);
        SetStatus(new Text("Scan tool connected: OBD-II mode 01 every 300 ms on 0x7DF → 0x7E8.", "Scan tool terhubung: OBD-II mode 01 tiap 300 ms di 0x7DF → 0x7E8."));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var rpm = (await _obd!.ReadPidAsync(ObdPids.EngineRpm, ct)).Value;
                var speed = (await _obd.ReadPidAsync(ObdPids.VehicleSpeed, ct)).Value;
                var coolant = (await _obd.ReadPidAsync(ObdPids.CoolantTemperature, ct)).Value;
                double throttle = Throttle, load = Load, voltage = Voltage, fuel = Fuel, maf = Maf;
                if (tick++ % 3 == 0)
                {
                    throttle = (await _obd.ReadPidAsync(ObdPids.ThrottlePosition, ct)).Value;
                    load = (await _obd.ReadPidAsync(ObdPids.EngineLoad, ct)).Value;
                    voltage = (await _obd.ReadPidAsync(ObdPids.ModuleVoltage, ct)).Value;
                    fuel = (await _obd.ReadPidAsync(ObdPids.FuelLevel, ct)).Value;
                    maf = (await _obd.ReadPidAsync(ObdPids.MassAirFlow, ct)).Value;
                }
                if (tick % 10 == 0) await ReadDtcsAsync();
                RpmTrend.Add(rpm);
                SpeedTrend.Add(speed);
                CoolantTrend.Add(coolant);
                var frames = (_tester?.FramesSent ?? 0) + (_tester?.FramesReceived ?? 0);
                var session = _ecu?.Session.ToString() ?? "Default";
                var unlocked = _ecu?.SecurityUnlocked ?? false;
                Ui(() =>
                {
                    (Rpm, Speed, Coolant, Throttle, Load, Voltage, Fuel, Maf) = (rpm, speed, coolant, throttle, load, voltage, fuel, maf);
                    CanFrames = frames;
                    Session = session;
                    Unlocked = unlocked;
                });
            }
            catch (OperationCanceledException) { break; }
            catch (IoTComException ex) { Ui(() => Status = ex.Message); }
        }
    }

    /// <summary>UDS 0x19 02: reads DTCs with their status bits.</summary>
    public async Task ReadDtcsAsync()
    {
        if (_uds is null) return;
        var list = await _uds.ReadDtcsAsync();
        Ui(() =>
        {
            Dtcs.Clear();
            foreach (var d in list) Dtcs.Add(new DtcRow(d.ToString(), Describe(d.ToString()), d.Status.HasFlag(DtcStatus.Confirmed) ? "Confirmed" : d.Status.HasFlag(DtcStatus.Pending) ? "Pending" : $"0x{(byte)d.Status:X2}", d.Status.HasFlag(DtcStatus.Confirmed)));
            Mil = list.Any(d => d.Status.HasFlag(DtcStatus.WarningIndicatorRequested));
        });
    }

    private static string Describe(string code) => code switch
    {
        "P0301" => Loc.L("Cylinder 1 misfire detected", "Misfire terdeteksi di silinder 1"),
        "P0420" => Loc.L("Catalyst efficiency below threshold (bank 1)", "Efisiensi katalis di bawah ambang (bank 1)"),
        "P0217" => Loc.L("Engine coolant over-temperature", "Suhu pendingin mesin berlebih"),
        _ => "",
    };

    /// <summary>UDS 0x14: clears all DTCs — blocked by read-only mode unless writes are allowed.</summary>
    public async Task ClearDtcsAsync()
    {
        if (_uds is null) return;
        try
        {
            await _uds.ClearDtcsAsync();
            Overheating = false;
            SetStatus(new Text("DTCs cleared (UDS 0x14 FFFFFF).", "DTC dihapus (UDS 0x14 FFFFFF)."));
        }
        catch (ReadOnlyModeException)
        {
            SetStatus(new Text("Blocked: the tester is read-only. Tick \"Allow writes\" to clear codes.", "Diblokir: tester dalam mode read-only. Centang \"Izinkan penulisan\" untuk menghapus kode."));
        }
        await ReadDtcsAsync();
    }

    /// <summary>Simulates a cooling fault: coolant climbs and the ECU sets P0217.</summary>
    public void InjectOverheating(bool on)
    {
        if (_ecu is null) return;
        _ecu.Vehicle.CoolingFault = on ? 32 : 0;
        Overheating = on;
    }

    /// <summary>UDS 0x10 03 + 0x27 01/02: extended session and security access with the demo seed/key algorithm.</summary>
    public async Task UnlockAsync()
    {
        if (_uds is null) return;
        try
        {
            await _uds.StartSessionAsync(UdsSession.Extended);
            await _uds.SecurityAccessAsync(0x01, EcuSimulator.ComputeKey);
            SetStatus(new Text("Extended session, security level 1 unlocked (seed → key). Keep the session alive with TesterPresent.", "Session extended, security level 1 terbuka (seed → key). Pertahankan session dengan TesterPresent."));
            _ = KeepAliveAsync(_poll?.Token ?? CancellationToken.None);
        }
        catch (IoTComException ex)
        {
            Status = ex.Message;
        }
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        // TesterPresent (0x3E 80) every 2 s keeps the non-default session open (the ECU's S3 timer is 5 s).
        try
        {
            while (!ct.IsCancellationRequested && _uds is { Session: not UdsSession.Default })
            {
                await Task.Delay(2000, ct);
                await _uds.TesterPresentAsync(ct: ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IoTComException) { }
    }

    /// <summary>UDS 0x2E F198: writes the workshop code (needs extended session, security access and allowed writes).</summary>
    public async Task WriteWorkshopCodeAsync(string code)
    {
        if (_uds is null || string.IsNullOrWhiteSpace(code)) return;
        try
        {
            await _uds.WriteDataByIdentifierAsync(UdsDid.RepairShopCode, System.Text.Encoding.ASCII.GetBytes(code.Trim()));
            var back = await _uds.ReadStringAsync(UdsDid.RepairShopCode);
            Ui(() => WorkshopCode = back);
            SetStatus(new Text("Written with 0x2E F198 and read back with 0x22.", "Ditulis dengan 0x2E F198 dan dibaca ulang dengan 0x22."));
        }
        catch (ReadOnlyModeException)
        {
            SetStatus(new Text("Blocked locally: the tester is read-only.", "Diblokir di sisi tester: mode read-only."));
        }
        catch (UdsNegativeResponseException ex)
        {
            SetStatus(new Text($"ECU refused: {UdsNegativeResponseException.Describe(ex.ResponseCode)} (NRC 0x{(byte)ex.ResponseCode:X2}). Unlock first.",
                $"ECU menolak: {UdsNegativeResponseException.Describe(ex.ResponseCode)} (NRC 0x{(byte)ex.ResponseCode:X2}). Buka kunci dulu."));
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_poll is not null) await _poll.CancelAsync();
        foreach (var t in _taps) t.Dispose();
        if (_uds is not null) await _uds.DisposeAsync();
        if (_obd is not null) await _obd.DisposeAsync();
        if (_tester is not null) await _tester.DisposeAsync();
        if (_ecu is not null) await _ecu.DisposeAsync();
        _poll?.Dispose();
        (_uds, _obd, _tester, _ecu, _poll, _network, _taps) = (null, null, null, null, null, null, []);
        Status = "";
    }
}

/// <summary>A DTC row in the list.</summary>
public sealed record DtcRow(string Code, string Description, string State, bool Confirmed);
