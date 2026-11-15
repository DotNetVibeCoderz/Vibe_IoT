using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Adapters.Amqp;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Adapters.Zenoh;
using IoTCom.Net.Gallery.Infrastructure;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>Statistics of one broker lane.</summary>
public sealed partial class BrokerLane : ObservableObject
{
    private readonly ConcurrentQueue<double> _latencies = new();
    private long _expected = -1;

    internal BrokerLane(string name, string address) => (Name, Address) = (name, address);

    public string Name { get; }
    public string Address { get; }
    public TrendBuffer Trend { get; } = new(120, 0.5);

    [ObservableProperty] private long _received;
    [ObservableProperty] private long _lost;
    [ObservableProperty] private double _p50;
    [ObservableProperty] private double _p95;
    [ObservableProperty] private double _max;

    /// <summary>Recent arrivals as (time, sequence) for the lane animation.</summary>
    internal ConcurrentQueue<(long Ticks, long Seq)> Recent { get; } = new();

    internal void Arrive(long seq, double latencyMs)
    {
        _latencies.Enqueue(latencyMs);
        while (_latencies.Count > 400) _latencies.TryDequeue(out _);
        Recent.Enqueue((Stopwatch.GetTimestamp(), seq));
        while (Recent.Count > 80) Recent.TryDequeue(out _);
        var expected = Interlocked.Read(ref _expected);
        if (expected >= 0 && seq > expected) Interlocked.Add(ref _lostPending, seq - expected);
        Interlocked.Exchange(ref _expected, seq + 1);
        Interlocked.Increment(ref _receivedPending);
    }

    private long _lostPending, _receivedPending;

    internal void Publish()
    {
        var snapshot = _latencies.ToArray();
        Array.Sort(snapshot);
        double At(double q) => snapshot.Length == 0 ? 0 : snapshot[Math.Min(snapshot.Length - 1, (int)(q * snapshot.Length))];
        Received += Interlocked.Exchange(ref _receivedPending, 0);
        Lost += Interlocked.Exchange(ref _lostPending, 0);
        (P50, P95, Max) = (At(0.5), At(0.95), snapshot.Length == 0 ? 0 : snapshot[^1]);
        Trend.Add(P50);
    }

    internal void Reset()
    {
        _latencies.Clear();
        Interlocked.Exchange(ref _expected, -1);
        (Received, Lost, P50, P95, Max) = (0, 0, 0, 0, 0);
    }
}

/// <summary>
/// One telemetry stream through three messaging systems side by side — MQTT (embedded broker), AMQP 1.0 (mini broker) and
/// Zenoh (peer to peer, no broker) — with latency percentiles, throughput and loss per lane, and a bridge mode where MQTT
/// is the only entry point and a bridge republishes into the other two.
/// </summary>
public sealed partial class MultiBrokerDemo : GalleryDemo
{
    public override string Id => "multi-broker";
    public override Text Title => new("Pub/sub across brokers", "Pub/sub lintas broker");
    public override Text Summary => new(
        "The same truck telemetry flows through MQTT, AMQP 1.0 and Zenoh at once: compare latency percentiles, throughput and loss as you raise the rate, then switch to bridge mode, where only MQTT is published and a bridge feeds the others.",
        "Telemetri truk yang sama mengalir lewat MQTT, AMQP 1.0, dan Zenoh sekaligus: bandingkan persentil latensi, throughput, dan kehilangan saat laju dinaikkan, lalu beralih ke mode jembatan, di mana hanya MQTT yang dipublikasikan dan jembatan meneruskan ke yang lain.");
    public override Text Docs => new(
        "IoT systems rarely use one messaging system end to end. Devices speak MQTT to a broker; enterprise systems consume AMQP queues or Kafka topics; robots and edge clusters use Zenoh or DDS, peer to peer. IoTCom.Net puts all of them behind the same IPublisher/ISubscriber contracts, so a bridge is a loop: subscribe on one endpoint, publish on another.\n\nEvery message here carries a sequence number and the time it was sent, so each lane measures one-way latency (median, 95th percentile, maximum), throughput and gaps. MQTT and AMQP go through a broker over TCP on loopback; Zenoh's virtual network delivers between sessions directly. In bridge mode the producer only publishes to MQTT, and a bridge subscribed to MQTT republishes into AMQP and Zenoh — the extra hop shows in their latency.\n\nThe MQTT broker is MQTTnet's (MqttBroker), the AMQP broker is IoTCom's AmqpMiniBroker and the Zenoh sessions use VirtualZenohNetwork. NATS and Kafka adapters work the same way against real servers (IOTCOM_NATS_URL, IOTCOM_KAFKA_BOOTSTRAP) and are left out of the Gallery because they need one.",
        "Sistem IoT jarang memakai satu sistem pesan dari ujung ke ujung. Perangkat berbicara MQTT ke broker; sistem perusahaan mengonsumsi antrean AMQP atau topik Kafka; robot dan klaster edge memakai Zenoh atau DDS, peer to peer. IoTCom.Net menaruh semuanya di balik kontrak IPublisher/ISubscriber yang sama, sehingga jembatan hanyalah sebuah loop: berlangganan di satu endpoint, memublikasikan di endpoint lain.\n\nSetiap pesan di sini membawa nomor urut dan waktu pengirimannya, sehingga setiap jalur mengukur latensi satu arah (median, persentil ke-95, maksimum), throughput, dan celah. MQTT dan AMQP melewati broker lewat TCP di loopback; jaringan virtual Zenoh mengantar antarsesi secara langsung. Dalam mode jembatan, produsen hanya memublikasikan ke MQTT, dan jembatan yang berlangganan MQTT memublikasikan ulang ke AMQP dan Zenoh — hop tambahan itu terlihat pada latensinya.\n\nBroker MQTT adalah milik MQTTnet (MqttBroker), broker AMQP adalah AmqpMiniBroker milik IoTCom, dan sesi Zenoh memakai VirtualZenohNetwork. Adapter NATS dan Kafka bekerja dengan cara yang sama terhadap server sungguhan (IOTCOM_NATS_URL, IOTCOM_KAFKA_BOOTSTRAP) dan tidak dimasukkan ke Gallery karena membutuhkannya.");
    public override string Category => "Messaging";
    public override IReadOnlyList<string> Protocols => ["MQTT", "AMQP 1.0", "Zenoh", "Bridging"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/messaging-brokers.md";

    private const string Topic = "fleet/truck42/telemetry";
    private const string AmqpAddress = "fleet.truck42.telemetry";
    private MqttBroker? _mqttBroker;
    private MqttEndpoint? _mqttPub, _mqttSub, _bridgeIn;
    private AmqpMiniBroker? _amqpBroker;
    private AmqpEndpoint? _amqpPub, _amqpSub;
    private VirtualZenohNetwork? _zenoh;
    private ZenohSession? _zenohPub, _zenohSub;
    private IDisposable? _zenohSubscription;
    private CancellationTokenSource? _cts;
    private long _seq;

    public BrokerLane Mqtt { get; } = new("MQTT 3.1.1/5", "broker 127.0.0.1 · QoS 0");
    public BrokerLane Amqp { get; } = new("AMQP 1.0", "mini broker 127.0.0.1 · pre-settled");
    public BrokerLane Zenoh { get; } = new("Zenoh", "peer to peer · virtual network");

    public IReadOnlyList<BrokerLane> Lanes => [Mqtt, Amqp, Zenoh];

    [ObservableProperty] private int _rate = 50;
    [ObservableProperty] private bool _bridgeMode;
    [ObservableProperty] private long _published;

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static byte[] Stamp(long seq)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(b, seq);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), Stopwatch.GetTimestamp());
        return b;
    }

    private static void Arrive(BrokerLane lane, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16) return;
        var seq = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var sent = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        lane.Arrive(seq, Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
    }

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        foreach (var l in Lanes) l.Reset();
        Published = _seq = 0;

        var mqttPort = FreePort();
        _mqttBroker = MqttBroker.Create(mqttPort);
        await _mqttBroker.StartAsync();
        _mqttPub = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", mqttPort).WithClientId("truck42"));
        _mqttPub.AddTap(Tap);
        _mqttSub = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", mqttPort).WithClientId("dashboard"));
        _bridgeIn = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", mqttPort).WithClientId("bridge"));
        await _mqttPub.ConnectAsync();
        await _mqttSub.ConnectAsync();
        await _bridgeIn.ConnectAsync();

        _amqpBroker = AmqpMiniBroker.Create();
        await _amqpBroker.StartAsync();
        _amqpPub = AmqpEndpoint.Create(o => o.UseBroker(_amqpBroker.Address));
        _amqpSub = AmqpEndpoint.Create(o => o.UseBroker(_amqpBroker.Address));
        await _amqpPub.ConnectAsync();
        await _amqpSub.ConnectAsync();

        _zenoh = new VirtualZenohNetwork();
        _zenohPub = ZenohSession.Create(o => o.UseVirtual(_zenoh));
        _zenohSub = ZenohSession.Create(o => o.UseVirtual(_zenoh));
        await _zenohPub.ConnectAsync();
        await _zenohSub.ConnectAsync();

        _ = Task.Run(async () =>
        {
            await foreach (var m in _mqttSub.SubscribeAsync(Topic, ct)) Arrive(Mqtt, m.Payload.Span);
        }, ct);
        _ = Task.Run(async () =>
        {
            await foreach (var m in _amqpSub.SubscribeAsync(AmqpAddress, ct)) Arrive(Amqp, m.Payload.Span);
        }, ct);
        _zenohSubscription = _zenohSub.Subscribe(Topic, s => Arrive(Zenoh, s.Payload));
        _ = Task.Run(() => BridgeAsync(ct), ct);
        _ = Task.Run(() => ProduceAsync(ct), ct);
        _ = Task.Run(() => StatsAsync(ct), ct);
        AddLog($"MQTT broker on 127.0.0.1:{mqttPort}, AMQP mini broker on {_amqpBroker.Address}, Zenoh virtual network with two sessions");
        SetStatus(new Text($"Topic {Topic} (MQTT, Zenoh) and address {AmqpAddress} (AMQP) · 16-byte payload: sequence + send time · latency is one-way, in this process.",
            $"Topik {Topic} (MQTT, Zenoh) dan alamat {AmqpAddress} (AMQP) · payload 16 byte: nomor urut + waktu kirim · latensi satu arah, di proses ini."));
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        var next = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var payload = Stamp(_seq++);
                await _mqttPub!.PublishAsync(Topic, payload, new PublishOptions { QualityOfService = QualityOfService.AtMostOnce }, ct);
                if (!BridgeMode)
                {
                    await _amqpPub!.PublishAsync(AmqpAddress, payload, new PublishOptions { QualityOfService = QualityOfService.AtMostOnce }, ct);
                    await _zenohPub!.PutAsync(Topic, payload, ct: ct);
                }

                Ui(() => Published = _seq);
                next += Stopwatch.Frequency / Math.Max(1, Rate);
                var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                else next = Stopwatch.GetTimestamp();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IoTComException)
        {
        }
    }

    /// <summary>In bridge mode, everything that arrives on MQTT is republished into AMQP and Zenoh (payload untouched).</summary>
    private async Task BridgeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var m in _bridgeIn!.SubscribeAsync(Topic, ct))
            {
                if (!BridgeMode) continue;
                await _amqpPub!.PublishAsync(AmqpAddress, m.Payload, new PublishOptions { QualityOfService = QualityOfService.AtMostOnce }, ct);
                await _zenohPub!.PutAsync(Topic, m.Payload.ToArray(), ct: ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IoTComException)
        {
        }
    }

    private async Task StatsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                Ui(() =>
                {
                    foreach (var l in Lanes) l.Publish();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    partial void OnBridgeModeChanged(bool value) => AddLog(value
        ? "Bridge mode: the truck publishes to MQTT only; a bridge republishes into AMQP and Zenoh"
        : "Direct mode: the truck publishes to all three");

    partial void OnRateChanged(int value) => AddLog($"Rate {value} messages/s");

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _zenohSubscription?.Dispose();
        foreach (var e in new IAsyncDisposable?[] { _mqttPub, _mqttSub, _bridgeIn, _amqpPub, _amqpSub, _zenohPub, _zenohSub, _amqpBroker, _mqttBroker })
            if (e is not null) await e.DisposeAsync();
        (_mqttPub, _mqttSub, _bridgeIn, _amqpPub, _amqpSub, _zenohPub, _zenohSub, _amqpBroker, _mqttBroker, _zenoh) = (null, null, null, null, null, null, null, null, null, null);
        _cts?.Dispose();
        _cts = null;
        Status = "";
    }
}
