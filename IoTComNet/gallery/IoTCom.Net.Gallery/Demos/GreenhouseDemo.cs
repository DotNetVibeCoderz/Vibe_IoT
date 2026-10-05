using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Smart greenhouse over CoAP: a constrained node (CoapDeviceSimulator) and the Gallery as client on an in-memory
/// UDP network whose packet loss you control. Sensors are observed (RFC 7641), actuators are PUT, the event log
/// arrives in blocks (RFC 7959) and calibration answers with a separate response. Every datagram — including the
/// ones the network drops — is drawn on a message sequence chart.
/// </summary>
public sealed partial class GreenhouseDemo : GalleryDemo
{
    public override string Id => "coap-greenhouse";
    public override Text Title => new("Smart greenhouse over CoAP", "Rumah kaca pintar lewat CoAP");
    public override Text Summary => new(
        "Observe a greenhouse node's sensors, switch its fan and irrigation valve, and push the link to 30 % packet loss: confirmable messages are retransmitted until acknowledged, duplicates are suppressed.",
        "Amati sensor node rumah kaca, nyalakan kipas dan katup irigasi, lalu buat jaringan kehilangan 30 % paket: pesan confirmable dikirim ulang sampai di-ACK, duplikat ditekan.");
    public override Text Docs => new(
        "CoAP is HTTP's idea — GET, PUT, POST, DELETE on resources, response codes, content formats — squeezed into UDP datagrams of a few bytes for battery-powered devices.\n\nUDP loses packets, so CoAP adds its own reliability: a Confirmable (CON) message is retransmitted with exponential back-off until an ACK arrives; a Non-confirmable (NON) one is sent once. Message IDs let the receiver drop duplicates and replay its cached answer. Tokens match responses to requests, even when the answer comes later as a separate response.\n\nObserve (RFC 7641) turns GET into a subscription: the device pushes a notification whenever the value changes. Block-wise transfer (RFC 7959) moves bodies larger than one datagram — here the event log. /.well-known/core lists every resource in CoRE Link Format.\n\nMove the loss slider and watch the chart: red broken arrows are datagrams the network dropped; the amber retransmission that follows is CoAP doing its job.",
        "CoAP adalah gagasan HTTP — GET, PUT, POST, DELETE pada resource, kode respons, content format — yang dipadatkan ke datagram UDP beberapa byte untuk perangkat bertenaga baterai.\n\nUDP bisa kehilangan paket, jadi CoAP menambah keandalannya sendiri: pesan Confirmable (CON) dikirim ulang dengan back-off eksponensial sampai ACK tiba; pesan Non-confirmable (NON) dikirim sekali. Message ID membuat penerima bisa membuang duplikat dan memutar ulang jawaban yang disimpan. Token mencocokkan respons dengan request, bahkan bila jawaban datang belakangan sebagai separate response.\n\nObserve (RFC 7641) mengubah GET menjadi langganan: perangkat mendorong notifikasi setiap kali nilai berubah. Block-wise transfer (RFC 7959) memindahkan isi yang lebih besar dari satu datagram — di sini log kejadian. /.well-known/core mendaftar semua resource dalam CoRE Link Format.\n\nGeser slider kehilangan paket dan lihat diagram: panah merah terputus adalah datagram yang dibuang jaringan; pengiriman ulang berwarna amber yang menyusul adalah CoAP yang sedang bekerja.");
    public override string Category => "Building";
    public override IReadOnlyList<string> Protocols => ["CoAP", "Observe", "Block-wise", "SenML"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/coap.md";

    private static readonly IPEndPoint NodeAddress = new(IPAddress.Parse("10.0.0.40"), 5683);
    private InMemoryDatagramNetwork? _network;
    private CoapServer? _server;
    private CoapDeviceSimulator? _device;
    private CoapClient? _client;
    private CancellationTokenSource? _cts;
    private EndPoint? _clientAddress;

    public TrendBuffer TemperatureTrend { get; } = new(120, 1);
    public TrendBuffer HumidityTrend { get; } = new(120, 4);
    public TrendBuffer SoilTrend { get; } = new(120, 4);

    /// <summary>The message sequence chart (newest first).</summary>
    public ObservableCollection<WireRow> Wire { get; } = [];

    /// <summary>Resources found by discovery.</summary>
    public ObservableCollection<string> Resources { get; } = [];

    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _humidity;
    [ObservableProperty] private double _soil;
    [ObservableProperty] private bool _fanOn;
    [ObservableProperty] private int _valve;
    [ObservableProperty] private double _lossPercent;
    [ObservableProperty] private long _sent;
    [ObservableProperty] private long _retransmissions;
    [ObservableProperty] private long _duplicates;
    [ObservableProperty] private long _lost;
    [ObservableProperty] private long _notifications;
    [ObservableProperty] private string _clientLabel = "client";
    [ObservableProperty] private string _lastAction = "";

    partial void OnLossPercentChanged(double value)
    {
        if (_network is not null) _network.LossRate = value / 100;
    }

    protected override async Task OnStartAsync()
    {
        // 1) An in-memory "UDP" network whose loss rate the slider controls.
        _network = new InMemoryDatagramNetwork(seed: 11) { LossRate = LossPercent / 100 };
        _network.Transmitted += OnTransmitted;

        // 2) The device: a CoAP server with the greenhouse resources, ticking once per second.
        var link = new CoapTransmission { AckTimeout = TimeSpan.FromMilliseconds(300), MaxRetransmit = 8, BlockSize = 256 };
        _server = CoapServer.Create(o => o.UseInMemory(_network, NodeAddress).Transmission = link);
        _server.AddTap(Tap);
        _device = new CoapDeviceSimulator(_server);
        await _server.StartAsync();
        _device.Start();

        // 3) The client: confirmable requests, ACK_TIMEOUT 400 ms so retransmissions are easy to see.
        _client = CoapClient.Create(o =>
        {
            o.UseInMemory(_network).UseServer(NodeAddress);
            o.Transmission = new CoapTransmission { AckTimeout = TimeSpan.FromMilliseconds(300), MaxRetransmit = 8, BlockSize = 256 };
        });
        await _client.ConnectAsync();
        _clientAddress = null;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var links = await _client.DiscoverAsync(ct: ct);
        Ui(() =>
        {
            Resources.Clear();
            foreach (var l in links) Resources.Add(l.Path + (l.Observable ? "  ◉" : ""));
        });
        _ = ObserveLoopAsync("/sensors/temperature", v => { TemperatureTrend.Add(v); Ui(() => Temperature = v); }, ct);
        _ = ObserveLoopAsync("/sensors/humidity", v => { HumidityTrend.Add(v); Ui(() => Humidity = v); }, ct);
        _ = ObserveLoopAsync("/sensors/soil", v => { SoilTrend.Add(v); Ui(() => Soil = v); }, ct);
        _ = StatsLoopAsync(ct);
        SetStatus(new Text("Observing 3 sensors on coap://10.0.0.40/ — notifications are NON, every 5th is CON.", "Mengamati 3 sensor di coap://10.0.0.40/ — notifikasi NON, tiap ke-5 CON."));
    }

    private async Task ObserveLoopAsync(string path, Action<double> update, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var n in _client!.ObserveAsync(path, ct: ct))
                {
                    if (double.TryParse(n.PayloadText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) update(v);
                    Ui(() => Notifications++);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (IoTComException)
            {
                // Registration lost on a very lossy link: register again.
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var c = _client!.Statistics;
                var s = _server!.Statistics;
                Ui(() =>
                {
                    Sent = c.MessagesSent + s.MessagesSent;
                    Retransmissions = c.RetransmissionCount + s.RetransmissionCount;
                    Duplicates = c.DuplicateCount + s.DuplicateCount;
                    Lost = _network?.Dropped ?? 0;
                    FanOn = _device?.FanOn ?? false;
                    Valve = _device?.ValvePercent ?? 0;
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnTransmitted(IPEndPoint from, EndPoint to, ReadOnlyMemory<byte> data, bool delivered)
    {
        if (!CoapMessage.TryDecode(data.Span, out var m, out _)) return;
        var fromClient = !from.Equals(NodeAddress);
        if (fromClient && _clientAddress is null)
        {
            _clientAddress = from;
            Ui(() => ClientLabel = from.ToString());
        }
        var row = WireRow.From(m, fromClient, delivered);
        Ui(() =>
        {
            Wire.Insert(0, row);
            while (Wire.Count > 16) Wire.RemoveAt(Wire.Count - 1);
        });
    }

    /// <summary>PUT /actuators/fan.</summary>
    public Task SetFanAsync(bool on) => Act(async ct =>
    {
        var r = await _client!.PutAsync("/actuators/fan", on ? "on" : "off", ct);
        return $"PUT /actuators/fan {(on ? "on" : "off")} → {r.Code}";
    });

    /// <summary>PUT /actuators/valve.</summary>
    public Task SetValveAsync(int percent) => Act(async ct =>
    {
        var r = await _client!.PutAsync("/actuators/valve", percent.ToString(CultureInfo.InvariantCulture), ct);
        return $"PUT /actuators/valve {percent} → {r.Code}";
    });

    /// <summary>GET /device/log — several Block2 exchanges.</summary>
    public Task FetchLogAsync() => Act(async ct =>
    {
        var r = await _client!.GetAsync("/device/log", ct: ct);
        var blocks = (r.Payload.Length + 255) / 256;
        return $"GET /device/log → {r.Code}, {r.Payload.Length} B in {blocks} blocks of 256 B";
    });

    /// <summary>POST /device/calibrate — the node answers with an empty ACK first, then a separate response.</summary>
    public Task CalibrateAsync() => Act(async ct =>
    {
        var started = DateTime.UtcNow;
        var r = await _client!.PostAsync("/device/calibrate", default, ct: ct);
        return $"POST /device/calibrate → {r.PayloadText} after {(DateTime.UtcNow - started).TotalSeconds:0.0} s (separate response)";
    });

    private async Task Act(Func<CancellationToken, Task<string>> action)
    {
        if (_client is null || _cts is null) return;
        try
        {
            var text = await action(_cts.Token);
            Ui(() => LastAction = text);
        }
        catch (IoTComException ex)
        {
            Ui(() => LastAction = ex.Message);
        }
        catch (OperationCanceledException) { }
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_network is not null) _network.Transmitted -= OnTransmitted;
        if (_client is not null) await _client.DisposeAsync();
        if (_device is not null) await _device.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        _cts?.Dispose();
        (_client, _device, _server, _network, _cts) = (null, null, null, null, null);
        Status = "";
    }
}

/// <summary>One datagram on the sequence chart.</summary>
/// <param name="Time">Wall-clock time.</param>
/// <param name="FromClient">Direction: client → node.</param>
/// <param name="Kind">CON, NON, ACK or RST.</param>
/// <param name="Label">What it carried.</param>
/// <param name="Delivered">False when the network dropped it.</param>
public sealed record WireRow(string Time, bool FromClient, string Kind, string Label, bool Delivered)
{
    internal static WireRow From(CoapMessage m, bool fromClient, bool delivered)
    {
        var kind = m.Type switch { CoapType.Confirmable => "CON", CoapType.NonConfirmable => "NON", CoapType.Acknowledgement => "ACK", _ => "RST" };
        string label;
        if (m.Code.Value == 0) label = $"empty · mid {m.MessageId:X4}";
        else if (m.Code.IsRequest) label = $"{m.Code} {m.UriPath}{(m.Observe == 0 ? " · observe" : "")}{(m.Block2 is { } b ? $" · block {b.Number}" : "")}{(m.Payload.IsEmpty ? "" : $" · \"{Short(m.PayloadText)}\"")}";
        else label = $"{m.Code}{(m.Observe is { } o ? $" · obs {o}" : "")}{(m.Block2 is { } b2 ? $" · block {b2.Number}{(b2.More ? "+" : "")}" : "")}{(m.Payload.IsEmpty || m.Block2 is not null ? "" : $" · \"{Short(m.PayloadText)}\"")}";
        return new WireRow(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), fromClient, kind, label, delivered);
    }

    private static string Short(string s) => s.Length <= 14 ? s : s[..14] + "…";
}
