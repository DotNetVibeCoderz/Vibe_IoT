using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Protocols.Sparkplug;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// A plant floor network: an mDNS browser finds the devices on the segment (Modbus gateway, MQTT broker, CoAP sensor…),
/// then a Sparkplug B host application follows a bottling line's edge node through births, data, commands and death.
/// </summary>
public sealed partial class PlantNetworkDemo : GalleryDemo
{
    public override string Id => "plant-network";
    public override Text Title => new("Plant network · Sparkplug B", "Jaringan pabrik · Sparkplug B");
    public override Text Summary => new(
        "Find what is plugged into the plant network with mDNS, then follow a bottling line through Sparkplug B: births, data by exception, commands, and the death certificate when the cable is pulled.",
        "Temukan apa saja yang tersambung ke jaringan pabrik dengan mDNS, lalu ikuti lini pembotolan lewat Sparkplug B: birth, data by exception, perintah, dan death certificate saat kabel dicabut.");
    public override Text Docs => new(
        "mDNS (RFC 6762) and DNS-SD (RFC 6763) let devices announce themselves without a DNS server: a browser multicasts a question for a service type such as _mqtt._tcp to 224.0.0.251:5353 and every device of that type answers with its name (PTR), host and port (SRV), properties (TXT) and address (A). When a device leaves it sends a goodbye (TTL 0).\n\nSparkplug B gives MQTT a state model. An edge node registers its NDEATH as the MQTT will, then publishes NBIRTH and one DBIRTH per device listing every metric with its type and a numeric alias. After that it reports only changes (NDATA/DDATA) using aliases, with a sequence number 0–255 across all its messages. A host application tracks births, resolves aliases, asks for a rebirth when a sequence number jumps, and sends writes as NCMD/DCMD. If the connection drops, the broker publishes the NDEATH will and every metric of that node becomes stale.\n\nTry it: pull the cable and watch the lamps go red without the edge node saying anything; plug it back in and the births repopulate the tree.",
        "mDNS (RFC 6762) dan DNS-SD (RFC 6763) memungkinkan perangkat mengumumkan dirinya tanpa server DNS: browser mengirim pertanyaan multicast untuk tipe layanan seperti _mqtt._tcp ke 224.0.0.251:5353 dan setiap perangkat bertipe itu menjawab dengan nama (PTR), host dan port (SRV), properti (TXT), dan alamat (A). Saat perangkat pergi, ia mengirim goodbye (TTL 0).\n\nSparkplug B memberi MQTT model state. Edge node mendaftarkan NDEATH sebagai will MQTT, lalu menerbitkan NBIRTH dan satu DBIRTH per perangkat yang mencantumkan setiap metrik dengan tipe dan alias numeriknya. Setelah itu ia hanya melaporkan perubahan (NDATA/DDATA) memakai alias, dengan nomor urut 0–255 di semua pesannya. Host application melacak birth, menerjemahkan alias, meminta rebirth saat nomor urut melompat, dan mengirim penulisan sebagai NCMD/DCMD. Jika koneksi putus, broker menerbitkan will NDEATH dan semua metrik node itu menjadi basi.\n\nCoba: cabut kabel dan lihat lampu menjadi merah tanpa edge node mengirim apa pun; sambungkan lagi dan birth mengisi ulang pohonnya.");
    public override string Category => "Messaging";
    public override IReadOnlyList<string> Protocols => ["mDNS", "DNS-SD", "Sparkplug B", "MQTT", "Protobuf"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/sparkplug.md";

    private MdnsSimulator? _plant;
    private MdnsBrowser? _browser;
    private MqttBroker? _broker;
    private SparkplugEdgeNode? _node;
    private SparkplugLineSimulator? _line;
    private SparkplugHost? _host;
    private CancellationTokenSource? _cts;
    private int _port;

    /// <summary>Devices discovered by mDNS.</summary>
    public ObservableCollection<DiscoveredRow> Discovered { get; } = [];

    /// <summary>The Unified Namespace: one row per node or device with its metrics.</summary>
    public ObservableCollection<UnsRow> Namespace { get; } = [];

    /// <summary>Sparkplug messages, newest first.</summary>
    public ObservableCollection<SparkplugLogRow> WireLog { get; } = [];

    [ObservableProperty] private bool _cableIn = true;
    [ObservableProperty] private bool _acceptWrites = true;
    [ObservableProperty] private string _lastAction = "";
    [ObservableProperty] private int _rebirths;
    [ObservableProperty] private string _bdSeq = "0";

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Discovered.Clear();
        Namespace.Clear();
        WireLog.Clear();

        // 1. Who is on the network?
        _plant = new MdnsSimulator();
        await _plant.StartAsync(ct);
        _browser = _plant.Browser();
        _browser.AddTap(Tap);
        _browser.ServiceChanged += (_, c) => Ui(() =>
        {
            var existing = Discovered.FirstOrDefault(d => d.Instance == c.Service.Instance);
            if (existing is not null) Discovered.Remove(existing);
            if (!c.Lost) Discovered.Add(new DiscoveredRow(c.Service));
        });
        await _browser.StartAsync(ct);
        foreach (var type in MdnsSimulator.Devices.Select(d => d.Type).Distinct()) _ = _browser.BrowseContinuouslyAsync(type, ct);

        // 2. The broker found above (simulated here on localhost) carries the Sparkplug traffic.
        _port = FreePort();
        _broker = MqttBroker.Create(_port);
        await _broker.StartAsync(ct);
        _host = SparkplugHost.Create(o => { o.HostId = "gallery-scada"; o.Mqtt = m => m.UseBroker("127.0.0.1", _port).WithClientId("gallery-scada"); });
        _host.MessageReceived += (_, e) => Ui(() =>
        {
            if (e.Topic.Type == SparkplugMessageType.State) return;
            WireLog.Insert(0, new SparkplugLogRow(e.Topic, e.Payload));
            while (WireLog.Count > 14) WireLog.RemoveAt(WireLog.Count - 1);
        });
        _host.StateChanged += (_, _) => Ui(RefreshNamespace);
        _host.MetricUpdated += (_, _) => Ui(RefreshNamespace);
        await _host.StartAsync(ct);
        _host.Mqtt?.AddTap(Tap);

        await StartNodeAsync(ct);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1200, ct);
                    if (_node?.Mqtt is not null && _line is not null) await _line.Step(ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IoTComException)
            {
            }
        }, ct);
        SetStatus(new Text($"mDNS on a simulated segment (10.20.0.0/24) · MQTT broker 127.0.0.1:{_port} · edge node Plant/Line1 with devices Filler and Tank7.",
            $"mDNS di segmen simulasi (10.20.0.0/24) · broker MQTT 127.0.0.1:{_port} · edge node Plant/Line1 dengan perangkat Filler dan Tank7."));
    }

    private async Task StartNodeAsync(CancellationToken ct)
    {
        var bdSeq = _node?.BdSeq ?? 0;
        _node = SparkplugEdgeNode.Create(o =>
        {
            o.Group = "Plant";
            o.EdgeNode = "Line1";
            o.BdSeq = bdSeq;
            o.AcceptWrites = AcceptWrites;
            o.Mqtt = m => m.UseBroker("127.0.0.1", _port).WithClientId("line1-edge");
        });
        _line = new SparkplugLineSimulator(_node).Define();
        await _node.StartAsync(ct);
        Ui(() => BdSeq = _node.BdSeq.ToString(CultureInfo.InvariantCulture));
    }

    private void RefreshNamespace()
    {
        if (_host is null) return;
        Namespace.Clear();
        foreach (var v in _host.Views.OrderBy(v => v.Key, StringComparer.Ordinal))
            Namespace.Add(new UnsRow(v.Device ?? $"{v.Group} / {v.EdgeNode}", v.Device is not null, v.Online,
                [.. v.Metrics.Values.Where(m => m.Name is not ("bdSeq" or "Node Control/Rebirth")).OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => new MetricCell(m))]));
        Rebirths = _host.RebirthRequests;
    }

    /// <summary>Pulls the edge node's network cable (the broker publishes the NDEATH will) or plugs it back in.</summary>
    public async Task ToggleCableAsync()
    {
        if (_node is null) return;
        if (CableIn)
        {
            await _node.DropConnectionAsync();
            CableIn = false;
            LastAction = Loc.L("Cable pulled: the edge node said nothing, the broker published its NDEATH will.", "Kabel dicabut: edge node tidak mengirim apa pun, broker menerbitkan will NDEATH-nya.");
        }
        else
        {
            await StartNodeAsync(_cts?.Token ?? default);
            CableIn = true;
            LastAction = Loc.L($"Plugged in: new session with bdSeq {BdSeq}, NBIRTH and DBIRTHs republished.", $"Tersambung: sesi baru dengan bdSeq {BdSeq}, NBIRTH dan DBIRTH diterbitkan ulang.");
        }
    }

    /// <summary>Writes a boolean metric from the host (DCMD).</summary>
    public async Task WriteAsync(string device, string metric, bool value)
    {
        if (_host is null) return;
        try
        {
            await _host.WriteAsync("Plant", "Line1", device, metric, value);
            LastAction = Loc.L($"DCMD {device}/{metric} = {value}", $"DCMD {device}/{metric} = {value}") + (AcceptWrites ? "" : Loc.L(" — ignored: the edge node is read-only.", " — diabaikan: edge node hanya-baca."));
        }
        catch (KeyNotFoundException ex)
        {
            LastAction = ex.Message;
        }
    }

    /// <summary>The current value of a boolean device metric as the host sees it.</summary>
    public bool Current(string device, string metric) => _host?.Find("Plant", "Line1", device)?.Metrics.TryGetValue(metric, out var m) == true && m.Value is true;

    /// <summary>Asks the edge node to republish its births.</summary>
    public async Task RebirthAsync()
    {
        if (_host is null) return;
        await _host.RequestRebirthAsync("Plant", "Line1");
        LastAction = Loc.L("NCMD Node Control/Rebirth = true", "NCMD Node Control/Rebirth = true");
    }

    /// <summary>Unplugs or plugs the label printer (mDNS goodbye / announcement).</summary>
    public async Task TogglePrinterAsync()
    {
        if (_plant is null) return;
        var online = Discovered.Any(d => d.Instance == "Label printer");
        if (online) await _plant.UnplugAsync("Label printer");
        else await _plant.PlugAsync("Label printer");
        LastAction = online ? Loc.L("Printer unplugged: it sent an mDNS goodbye (TTL 0).", "Printer dicabut: ia mengirim goodbye mDNS (TTL 0).") : Loc.L("Printer back: it announced itself again.", "Printer kembali: ia mengumumkan dirinya lagi.");
    }

    partial void OnAcceptWritesChanged(bool value)
    {
        if (_node is null || !CableIn) return;
        // The option is read when the session starts: reconnect to apply it.
        _ = Task.Run(async () =>
        {
            await _node.StopAsync();
            await StartNodeAsync(_cts?.Token ?? default);
        });
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_node is not null) await _node.DisposeAsync();
        if (_host is not null) await _host.DisposeAsync();
        if (_broker is not null) await _broker.DisposeAsync();
        if (_browser is not null) await _browser.DisposeAsync();
        if (_plant is not null) await _plant.DisposeAsync();
        (_node, _host, _broker, _browser, _plant, _line) = (null, null, null, null, null, null);
        _cts?.Dispose();
        _cts = null;
        CableIn = true;
        Status = "";
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}

/// <summary>A device found by mDNS.</summary>
public sealed record DiscoveredRow(string Instance, string Type, string Endpoint, string Txt)
{
    public DiscoveredRow(MdnsService s) : this(s.Instance, s.Type, $"{s.Address}:{s.Port}", string.Join("  ", s.Properties.Select(p => $"{p.Key}={p.Value}")))
    {
    }
}

/// <summary>A node or device in the Unified Namespace.</summary>
public sealed record UnsRow(string Name, bool IsDevice, bool Online, IReadOnlyList<MetricCell> Metrics);

/// <summary>One metric value.</summary>
public sealed record MetricCell(string Name, string Value, string Type)
{
    public MetricCell(SparkplugMetric m) : this(m.Name ?? "?", Format(m), m.DataType.ToString())
    {
    }

    private static string Format(SparkplugMetric m) => m.Value switch
    {
        null => "null",
        float f => f.ToString("0.0", CultureInfo.InvariantCulture),
        double d => d.ToString("0.00", CultureInfo.InvariantCulture),
        bool b => b ? "ON" : "OFF",
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        var v => v.ToString() ?? "",
    };
}

/// <summary>A Sparkplug message in the log.</summary>
public sealed record SparkplugLogRow(string Time, string Kind, string Topic, string Detail)
{
    public SparkplugLogRow(SparkplugTopic t, SparkplugPayload? p) : this(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), t.Type.ToString().ToUpperInvariant(),
        t.Device is null ? $"{t.Group}/{t.EdgeNode}" : $"{t.Group}/{t.EdgeNode}/{t.Device}",
        p is null ? "" : $"{(p.Seq is { } s ? $"seq {s} · " : "")}{string.Join(", ", p.Metrics.Take(4).Select(m => m.Name ?? $"#{m.Alias}"))}{(p.Metrics.Count > 4 ? " …" : "")}")
    {
    }
}
