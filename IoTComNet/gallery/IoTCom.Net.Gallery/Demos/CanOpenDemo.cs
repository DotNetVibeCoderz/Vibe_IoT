using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.CanOpen;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// A CANopen I/O network: two CiA 401-style modules on a virtual CAN bus, a master that follows their heartbeats,
/// switches them with NMT, decodes their PDOs and reads/writes their object dictionaries over SDO.
/// </summary>
public sealed partial class CanOpenDemo : GalleryDemo
{
    public override string Id => "canopen-io";
    public override Text Title => new("CANopen I/O modules", "Modul I/O CANopen");
    public override Text Summary => new(
        "Two I/O modules on a CAN bus: start them with NMT, watch their inputs arrive in PDOs, switch the pump output, and read any object over SDO.",
        "Dua modul I/O di bus CAN: jalankan dengan NMT, lihat inputnya datang lewat PDO, nyalakan output pompa, dan baca objek apa pun lewat SDO.");
    public override Text Docs => new(
        "CANopen (CiA 301) adds a device model on top of CAN. Every node has an object dictionary addressed by index and sub-index: identity, parameters, process data. A master reads and writes it with SDO, a confirmed request/response protocol that splits values longer than 4 bytes into segments.\n\nProcess data travels in PDOs: up to 8 bytes with no protocol overhead, sent when a value changes, on an event timer, or on every SYNC. The PDO mapping (objects 0x1A00/0x1600) says which objects sit where. NMT moves nodes between pre-operational (SDO only), operational (PDOs flow) and stopped; heartbeats report the state every few hundred milliseconds, so the master notices a node that disappears.\n\nThe modules here are simulated with IoTCom.Net's CanOpenNode; the master is CanOpenMaster, the same code that drives real nodes through SocketCAN, slcan, gs_usb or PCAN.",
        "CANopen (CiA 301) menambahkan model perangkat di atas CAN. Setiap node punya object dictionary yang dialamatkan dengan index dan sub-index: identitas, parameter, data proses. Master membaca dan menulisnya dengan SDO, protokol request/response terkonfirmasi yang memecah nilai lebih dari 4 byte menjadi segmen.\n\nData proses dikirim lewat PDO: hingga 8 byte tanpa overhead protokol, dikirim saat nilai berubah, menurut event timer, atau pada setiap SYNC. Mapping PDO (objek 0x1A00/0x1600) menyatakan objek mana berada di posisi mana. NMT memindahkan node antara pre-operational (hanya SDO), operational (PDO mengalir), dan stopped; heartbeat melaporkan state setiap beberapa ratus milidetik, sehingga master menyadari node yang hilang.\n\nModul di sini disimulasikan dengan CanOpenNode milik IoTCom.Net; masternya CanOpenMaster, kode yang sama yang menggerakkan node sungguhan lewat SocketCAN, slcan, gs_usb, atau PCAN.");
    public override string Category => "Industrial";
    public override IReadOnlyList<string> Protocols => ["CANopen", "CiA 301", "CAN"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/canopen.md";

    private VirtualCanNetwork? _net;
    private readonly List<CanOpenIoModuleSimulator> _modules = [];
    private CanOpenMaster? _reader;
    private CanOpenMaster? _writer;
    private VirtualCanBus? _bus;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<byte, PdoMapping> _mappings = [];

    /// <summary>Module faceplates.</summary>
    public ObservableCollection<IoModuleView> Modules { get; } = [];

    /// <summary>PDO/NMT log, newest first.</summary>
    public ObservableCollection<string> BusLog { get; } = [];

    [ObservableProperty] private bool _allowWrite;
    [ObservableProperty] private string _lastAction = "";
    [ObservableProperty] private string _sdoAddress = "1008";
    [ObservableProperty] private string _sdoResult = "";

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Modules.Clear();
        BusLog.Clear();
        _modules.Clear();
        _net = new VirtualCanNetwork("gallery-canopen");
        foreach (byte id in new byte[] { 5, 6 })
        {
            var m = CanOpenIoModuleSimulator.Create(_net.CreateNode(), id, heartbeatMs: 300, eventTimerMs: 400, seed: id);
            if (id == 6) m.Node.Dictionary.Set(0x2100, 0, "Pump skid 3, Karawang");
            _modules.Add(m);
            Modules.Add(new IoModuleView(id));
        }

        _bus = _net.CreateNode();
        _bus.AddTap(Tap);
        _reader = CanOpenMaster.Create(_bus, o => o.ReadOnly = true);
        _reader.NodeStateChanged += n => Ui(() =>
        {
            Module(n.Id)?.SetState(n.State);
            Trace($"heartbeat node {n.Id}: {n.State}");
        });
        _reader.PdoReceived += (node, pdo, data) => Ui(() => OnPdo(node, pdo, data));
        _reader.EmergencyReceived += (node, e) => Ui(() => Trace($"EMCY node {node}: {e}"));
        await _reader.StartAsync(ct);
        foreach (var m in _modules) await m.StartAsync(ct);

        await Task.Delay(400, ct);
        foreach (var v in Modules)
        {
            v.Name = (string)await _reader.ReadAsync(v.Node, 0x1008, 0, CanOpenDataType.VisibleString, ct);
            v.Location = (string)await _reader.ReadAsync(v.Node, 0x2100, 0, CanOpenDataType.VisibleString, ct);
            _mappings[v.Node] = await _reader.ReadTpdoMappingAsync(v.Node, 1, ct);
        }

        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            // Screenshots show the network running: both nodes operational, node 5's pump on (simulation side).
            await _bus.SendAsync(CanOpenCodec.Nmt(NmtCommand.Start, 0), ct);
            _modules[0].Node.Dictionary.Set(0x6200, 1, (byte)1);
            Modules[0].Outputs = 1;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    foreach (var m in _modules) m.Step();
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
        SetStatus(new Text("Virtual CAN bus · nodes 5 and 6 (I/O modules) · master with read-only and write sessions · heartbeat 300 ms, TPDO1 event timer 400 ms.",
            "Bus CAN virtual · node 5 dan 6 (modul I/O) · master dengan sesi hanya-baca dan tulis · heartbeat 300 ms, event timer TPDO1 400 ms."));
    }

    private IoModuleView? Module(byte node) => Modules.FirstOrDefault(m => m.Node == node);

    private void Trace(string line)
    {
        BusLog.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (BusLog.Count > 12) BusLog.RemoveAt(BusLog.Count - 1);
    }

    private void OnPdo(byte node, int pdo, byte[] data)
    {
        var view = Module(node);
        if (view is null) return;
        if (pdo == 1 && _mappings.TryGetValue(node, out var map))
        {
            var values = map.Unpack(data);
            view.Inputs = values[0].Raw[0];
            view.Pressure = BitConverter.ToInt16(values[1].Raw);
            view.Flow = BitConverter.ToInt16(values[2].Raw) / 10.0;
        }

        Trace($"TPDO{pdo} node {node}: {Convert.ToHexString(data)}");
    }

    private async Task<CanOpenMaster> WriterAsync()
    {
        if (!AllowWrite) return _reader!;
        if (_writer is null)
        {
            _writer = CanOpenMaster.Create(_net!.CreateNode());
            await _writer.StartAsync();
        }

        return _writer;
    }

    private async Task Guarded(Func<CanOpenMaster, Task> act, string done)
    {
        try
        {
            await act(await WriterAsync());
            LastAction = done;
        }
        catch (ReadOnlyModeException)
        {
            LastAction = Loc.L("Refused: the master is read-only. Tick “Allow writes” first.", "Ditolak: master hanya-baca. Centang “Izinkan penulisan” dulu.");
        }
        catch (CanOpenSdoException ex)
        {
            LastAction = ex.Message;
        }
    }

    /// <summary>Sends an NMT command to a node.</summary>
    public Task NmtAsync(byte node, NmtCommand command) => Guarded(m => m.NmtAsync(command, node), $"NMT {command} → node {node}");

    /// <summary>Toggles output 1 (the pump) over SDO 0x6200:01.</summary>
    public Task TogglePumpAsync(byte node)
    {
        var view = Module(node);
        var next = (byte)((view?.Outputs ?? 0) ^ 1);
        return Guarded(async m =>
        {
            await m.WriteAsync(node, 0x6200, 1, CanOpenDataType.Unsigned8, next);
            if (view is not null) view.Outputs = next;
        }, Loc.L($"SDO write node {node} 6200:01 = {next}", $"Tulis SDO node {node} 6200:01 = {next}"));
    }

    /// <summary>Reads the object in <see cref="SdoAddress"/> from node 5.</summary>
    public async Task ReadSdoAsync(byte node)
    {
        if (_reader is null) return;
        try
        {
            var parts = SdoAddress.Split(':');
            var index = ushort.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var sub = parts.Length > 1 ? byte.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)0;
            var raw = await _reader.UploadAsync(node, index, sub);
            var text = raw.Length > 4 && raw.All(b => b is >= 0x20 and < 0x7F) ? $"\"{System.Text.Encoding.ASCII.GetString(raw)}\"" : $"0x{Convert.ToHexString(raw.Reverse().ToArray())}";
            SdoResult = $"node {node} {index:X4}:{sub:X2} = {text} ({raw.Length} B{(raw.Length > 4 ? ", segmented" : ", expedited")})";
        }
        catch (Exception ex) when (ex is FormatException or IoTComException)
        {
            SdoResult = ex.Message;
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_writer is not null) await _writer.DisposeAsync();
        if (_reader is not null) await _reader.DisposeAsync();
        foreach (var m in _modules) await m.DisposeAsync();
        if (_bus is not null) await _bus.DisposeAsync();
        _modules.Clear();
        (_writer, _reader, _bus, _net) = (null, null, null, null);
        _cts?.Dispose();
        _cts = null;
        AllowWrite = false;
        Status = "";
    }
}

/// <summary>One I/O module faceplate.</summary>
public sealed partial class IoModuleView(byte node) : ObservableObject
{
    public byte Node { get; } = node;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _state = "?";
    [ObservableProperty] private byte _inputs;
    [ObservableProperty] private byte _outputs;
    [ObservableProperty] private int _pressure;
    [ObservableProperty] private double _flow;

    public void SetState(NmtState? s) => State = s?.ToString() ?? "?";
}
