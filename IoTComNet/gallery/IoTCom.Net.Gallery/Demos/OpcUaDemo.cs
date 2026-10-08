using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Adapters.OpcUa;
using IoTCom.Net.Gallery.Infrastructure;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// An OPC UA tag browser: a client connects to the plant simulator over a signed and encrypted channel, browses the
/// address space, subscribes to every variable and writes through a guarded path (read-only until writes are allowed).
/// </summary>
public sealed partial class OpcUaDemo : GalleryDemo
{
    public override string Id => "opcua-tags";
    public override Text Title => new("OPC UA tag browser", "Penjelajah tag OPC UA");
    public override Text Summary => new(
        "Browse a bottling line's OPC UA address space over an encrypted session, watch every tag through one subscription, and write setpoints only after you allow it.",
        "Jelajahi address space OPC UA sebuah lini pembotolan lewat sesi terenkripsi, pantau setiap tag lewat satu subscription, dan tulis setpoint hanya setelah Anda mengizinkannya.");
    public override Text Docs => new(
        "OPC UA organises a device as an address space: objects and folders contain variables (values with a data type, status and timestamps) and methods. Every node has a NodeId such as ns=2;s=Plant/Line1/Filler/Speed, where ns is the index of the namespace the node belongs to.\n\nA client browses references to discover the structure, reads values, and creates subscriptions: the server samples the monitored items and publishes only changes, so one subscription can follow hundreds of tags. Writes and method calls go through the same session.\n\nSecurity is part of the protocol: the client picks an endpoint (here Basic256Sha256 with SignAndEncrypt), both sides exchange application certificates, and the session can carry a user identity. IoTCom.Net does not reimplement any of this: the adapter wraps the OPC Foundation .NET Standard stack and adds the IoTCom endpoint model, a read-only switch, subscriptions as IAsyncEnumerable, and the plant simulator used here.",
        "OPC UA mengatur perangkat sebagai address space: objek dan folder berisi variabel (nilai dengan tipe data, status, dan timestamp) serta method. Setiap node punya NodeId seperti ns=2;s=Plant/Line1/Filler/Speed, dengan ns adalah indeks namespace tempat node berada.\n\nClient menjelajah referensi untuk menemukan strukturnya, membaca nilai, dan membuat subscription: server mengambil sampel monitored item dan hanya menerbitkan perubahan, sehingga satu subscription bisa mengikuti ratusan tag. Penulisan dan pemanggilan method lewat sesi yang sama.\n\nKeamanan adalah bagian dari protokol: client memilih endpoint (di sini Basic256Sha256 dengan SignAndEncrypt), kedua pihak bertukar sertifikat aplikasi, dan sesi bisa membawa identitas pengguna. IoTCom.Net tidak menulis ulang semua ini: adapter membungkus stack OPC Foundation .NET Standard dan menambahkan model endpoint IoTCom, saklar read-only, subscription sebagai IAsyncEnumerable, dan simulator pabrik yang dipakai di sini.");
    public override string Category => "Industrial";
    public override IReadOnlyList<string> Protocols => ["OPC UA", "Basic256Sha256"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/opcua.md";

    private OpcUaPlantServer? _server;
    private OpcUaClient? _reader;
    private OpcUaClient? _writer;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, TrendBuffer> _trends = new(StringComparer.Ordinal);

    /// <summary>The address space, flattened with depth for indentation.</summary>
    public ObservableCollection<TagRow> Tags { get; } = [];

    /// <summary>Trend of the selected numeric tag.</summary>
    public TrendBuffer SelectedTrend { get; private set; } = new(90, 1);

    [ObservableProperty] private TagRow? _selected;
    [ObservableProperty] private bool _allowWrite;
    [ObservableProperty] private bool _highLevel;
    [ObservableProperty] private string _session = "";
    [ObservableProperty] private string _lastAction = "";
    [ObservableProperty] private long _notifications;

    /// <summary>Raised when the selected trend is replaced.</summary>
    public event Action? TrendChanged;

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Tags.Clear();
        _trends.Clear();
        var pki = Path.Combine(Path.GetTempPath(), "iotcom-gallery-opcua");
        _server = OpcUaPlantServer.Create(o => { o.Port = FreePort(); o.Interval = TimeSpan.FromMilliseconds(500); o.PkiPath = Path.Combine(pki, "server"); });
        await _server.StartAsync(ct);
        _reader = OpcUaClient.Create(o =>
        {
            o.UseEndpoint(_server.EndpointUrl);
            o.AcceptUntrustedCertificates = true;
            o.ReadOnly = true;
            o.PkiPath = Path.Combine(pki, "client");
        });
        _reader.AddTap(Tap);
        await _reader.ConnectAsync(ct);
        Session = $"{_server.EndpointUrl} · {_reader.SecurityPolicy} / {_reader.SecurityMode} · {Loc.L("read-only session", "sesi hanya-baca")}";

        // Browse the plant into a flat, indented list.
        var rows = new List<TagRow>();
        async Task Walk(string? nodeId, int depth)
        {
            foreach (var n in await _reader.BrowseAsync(nodeId, ct))
            {
                if (n.NodeId.StartsWith("i=", StringComparison.Ordinal)) continue;
                rows.Add(new TagRow(n.NodeId, n.DisplayName, n.NodeClass, depth));
                if (n.IsContainer) await Walk(n.NodeId, depth + 1);
            }
        }

        await Walk(null, 0);
        foreach (var r in rows) Tags.Add(r);
        Selected = Tags.FirstOrDefault(t => t.Name == "Level");

        var variables = rows.Where(r => r.NodeClass == "Variable").Select(r => r.NodeId).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var v in _reader.SubscribeAsync(variables, TimeSpan.FromMilliseconds(250), ct))
                {
                    var number = v.Value is IConvertible c && v.Value is not (string or bool) ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : (double?)null;
                    if (number is { } d)
                    {
                        if (!_trends.TryGetValue(v.NodeId, out var trend)) _trends[v.NodeId] = trend = new TrendBuffer(90, 1);
                        trend.Add(d);
                    }

                    Ui(() =>
                    {
                        Notifications++;
                        var row = Tags.FirstOrDefault(t => t.NodeId == v.NodeId);
                        if (row is not null) row.Update(v);
                        if (row?.Name == "HighLevelAlarm") HighLevel = v.Value is true;
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IoTComException)
            {
            }
        }, ct);
        SetStatus(new Text($"Plant simulator and client in this process · {variables.Count} variables in one subscription (250 ms).",
            $"Simulator pabrik dan client di proses ini · {variables.Count} variabel dalam satu subscription (250 ms)."));
    }

    partial void OnSelectedChanged(TagRow? value)
    {
        if (value is null) return;
        if (!_trends.TryGetValue(value.NodeId, out var trend)) _trends[value.NodeId] = trend = new TrendBuffer(90, 1);
        SelectedTrend = trend;
        OnPropertyChanged(nameof(SelectedTrend));
        TrendChanged?.Invoke();
    }

    private async Task<OpcUaClient> WriterAsync()
    {
        if (!AllowWrite) return _reader!;          // writes go to the read-only session and are refused there
        if (_writer is null)
        {
            _writer = OpcUaClient.Create(o =>
            {
                o.UseEndpoint(_server!.EndpointUrl);
                o.AcceptUntrustedCertificates = true;
                o.PkiPath = Path.Combine(Path.GetTempPath(), "iotcom-gallery-opcua", "client");
            });
            _writer.AddTap(Tap);
            await _writer.ConnectAsync();
        }

        return _writer;
    }

    private string Node(string path) => _server!.NodeId(path);

    private bool Current(string path) => Tags.FirstOrDefault(t => t.NodeId == Node(path))?.Raw is true;

    private double CurrentNumber(string path) => Convert.ToDouble(Tags.FirstOrDefault(t => t.NodeId == Node(path))?.Raw ?? 0, System.Globalization.CultureInfo.InvariantCulture);

    private async Task Guarded(Func<OpcUaClient, Task> act, string done)
    {
        if (_server is null) return;
        try
        {
            await act(await WriterAsync());
            if (done.Length > 0) LastAction = done;
        }
        catch (ReadOnlyModeException)
        {
            LastAction = Loc.L("Refused by the client: the session is read-only. Tick “Allow writes” first.", "Ditolak oleh client: sesi hanya-baca. Centang “Izinkan penulisan” dulu.");
        }
        catch (DeviceException ex)
        {
            LastAction = ex.Message;
        }
    }

    /// <summary>Toggles the filler.</summary>
    public Task ToggleFillerAsync() => Guarded(c => c.WriteAsync(Node("Line1/Filler/Running"), !Current("Line1/Filler/Running")), Loc.L("Write Line1/Filler/Running: Good", "Tulis Line1/Filler/Running: Good"));

    /// <summary>Toggles the tank's inlet valve.</summary>
    public Task ToggleValveAsync() => Guarded(c => c.WriteAsync(Node("Line1/Tank7/InletValve"), !Current("Line1/Tank7/InletValve")), Loc.L("Write Line1/Tank7/InletValve: Good", "Tulis Line1/Tank7/InletValve: Good"));

    /// <summary>Changes the filler setpoint.</summary>
    public Task NudgeSetpointAsync(double delta) => Guarded(c => c.WriteAsync(Node("Line1/Filler/Setpoint"), Math.Clamp(CurrentNumber("Line1/Filler/Setpoint") + delta, 0, 200)),
        Loc.L($"Write Line1/Filler/Setpoint {delta:+0;-0}: Good", $"Tulis Line1/Filler/Setpoint {delta:+0;-0}: Good"));

    /// <summary>Calls ResetCounter.</summary>
    public Task ResetCounterAsync() => Guarded(async c =>
    {
        var outputs = await c.CallAsync(Node("Line1"), Node("Line1/ResetCounter"));
        LastAction = Loc.L($"ResetCounter() returned {outputs[0]}", $"ResetCounter() mengembalikan {outputs[0]}");
    }, "");

    partial void OnAllowWriteChanged(bool value) =>
        Session = _server is null || _reader is null ? "" : $"{_server.EndpointUrl} · {_reader.SecurityPolicy} / {_reader.SecurityMode} · {(value ? Loc.L("writes allowed", "penulisan diizinkan") : Loc.L("read-only session", "sesi hanya-baca"))}";

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_writer is not null) await _writer.DisposeAsync();
        if (_reader is not null) await _reader.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        (_writer, _reader, _server) = (null, null, null);
        _cts?.Dispose();
        _cts = null;
        AllowWrite = false;
        Status = "";
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }
}

/// <summary>A node of the address space with its live value.</summary>
public sealed partial class TagRow(string nodeId, string name, string nodeClass, int depth) : ObservableObject
{
    public string NodeId { get; } = nodeId;
    public string Name { get; } = name;
    public string NodeClass { get; } = nodeClass;
    public int Depth { get; } = depth;
    public object? Raw { get; private set; }

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _time = "";

    public string Glyph => NodeClass switch { "Variable" => "●", "Method" => "ƒ", _ => "▸" };

    public void Update(OpcUaValue v)
    {
        Raw = v.Value;
        Value = v.Value switch
        {
            double d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "ON" : "OFF",
            _ => v.Text,
        };
        Status = v.Status;
        Time = v.SourceTimestamp?.LocalDateTime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}
