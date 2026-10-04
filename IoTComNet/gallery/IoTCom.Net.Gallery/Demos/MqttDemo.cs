using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Serialization.SenML;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>A message shown in the subscriber log.</summary>
public sealed record MqttLogItem(string Time, string Topic, string Payload);

/// <summary>
/// Pub/Sub: an embedded MQTT broker, a sensor that publishes SenML every second, and a subscriber
/// that listens to a topic filter. Publish your own messages from the form.
/// </summary>
public sealed partial class MqttDemo : GalleryDemo
{
    public override string Id => "mqtt-pubsub";
    public override Text Title => new("MQTT publish & subscribe", "Publish & subscribe MQTT");
    public override Text Summary => new(
        "An embedded broker, a sensor publishing SenML every second, and a subscriber with wildcards. Publish your own messages and see how topic filters route them.",
        "Broker tertanam, sensor yang mengirim SenML tiap detik, dan subscriber dengan wildcard. Kirim pesan sendiri dan lihat bagaimana filter topik meneruskannya.");
    public override Text Docs => new(
        "MQTT routes messages by topic. Subscribers use filters: + matches one level (plant/+/temp), # matches the rest (plant/#).\n\nIoTCom.Net does not re-implement MQTT: MqttEndpoint is an adapter over MQTTnet that adds IAsyncEnumerable subscriptions, automatic reconnect with resubscribe, JSON/SenML helpers and the traffic tap. MqttBroker embeds MQTTnet's server for gateways, tests and demos like this one.\n\nThe sensor payload is SenML (RFC 8428), built with SenMLPackBuilder.",
        "MQTT meneruskan pesan berdasarkan topik. Subscriber memakai filter: + cocok dengan satu level (plant/+/temp), # cocok dengan sisanya (plant/#).\n\nIoTCom.Net tidak menulis ulang MQTT: MqttEndpoint adalah adapter di atas MQTTnet yang menambah subscription IAsyncEnumerable, reconnect otomatis dengan resubscribe, helper JSON/SenML dan traffic tap. MqttBroker menanamkan server MQTTnet untuk gateway, pengujian, dan demo seperti ini.\n\nPayload sensor adalah SenML (RFC 8428), dibuat dengan SenMLPackBuilder.");
    public override string Category => "Messaging";
    public override IReadOnlyList<string> Protocols => ["MQTT 5.0", "SenML"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/mqtt.md";

    private MqttBroker? _broker;
    private MqttEndpoint? _publisher;
    private MqttEndpoint? _subscriber;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _subscription;

    public ObservableCollection<MqttLogItem> Messages { get; } = [];

    [ObservableProperty] private string _topic = "plant/line1/note";
    [ObservableProperty] private string _payload = "Shift handover: line 1 OK";
    [ObservableProperty] private string _filter = "plant/#";
    [ObservableProperty] private bool _retain;
    [ObservableProperty] private int _port;

    protected override async Task OnStartAsync()
    {
        Port = FreePort();
        _broker = MqttBroker.Create(Port);
        await _broker.StartAsync();

        _publisher = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", Port).WithClientId("gallery-sensor"));
        _publisher.AddTap(Tap);
        await _publisher.ConnectAsync();
        _subscriber = MqttEndpoint.Create(o => o.UseBroker("127.0.0.1", Port).WithClientId("gallery-dashboard"));
        await _subscriber.ConnectAsync();
        await SubscribeAsync();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => SensorAsync(_cts.Token));
        SetStatus(new Text($"Broker on 127.0.0.1:{Port} · sensor publishes SenML to plant/line1/sensor every second.", $"Broker di 127.0.0.1:{Port} · sensor mengirim SenML ke plant/line1/sensor tiap detik."));
    }

    private async Task SensorAsync(CancellationToken ct)
    {
        var rng = new Random(3);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var pack = new SenMLPackBuilder("urn:dev:iotcom:line1:").At(DateTimeOffset.UtcNow)
                .Add("temperature", Math.Round(24 + rng.NextDouble() * 2, 2), "Cel")
                .Add("humidity", Math.Round(55 + rng.NextDouble() * 5, 1), "%RH")
                .Build();
            await _publisher!.PublishAsync("plant/line1/sensor", SenMLCodec.ToJson(pack), new PublishOptions { ContentType = SenMLCodec.JsonContentType }, ct);
        }
    }

    /// <summary>(Re)subscribes the dashboard client to <see cref="Filter"/>.</summary>
    public async Task SubscribeAsync()
    {
        if (_subscriber is null) return;
        if (_subscription is not null) await _subscription.CancelAsync();
        _subscription = new CancellationTokenSource();
        var token = _subscription.Token;
        var filter = Filter;
        AddLog($"SUBSCRIBE {filter}");
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var m in _subscriber.SubscribeStringAsync(filter, token))
                {
                    var item = new MqttLogItem(m.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), m.Topic, m.Payload);
                    Ui(() =>
                    {
                        Messages.Insert(0, item);
                        while (Messages.Count > 80) Messages.RemoveAt(Messages.Count - 1);
                    });
                }
            }
            catch (OperationCanceledException) { }
        }, token);
        await Task.Delay(100, token); // let SUBSCRIBE reach the broker before the next publish
    }

    /// <summary>Publishes the form's message.</summary>
    public async Task PublishAsync()
    {
        if (_publisher is null) return;
        await _publisher.PublishStringAsync(Topic, Payload, new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce, Retain = Retain });
        AddLog($"PUBLISH {Topic}");
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_subscription is not null) await _subscription.CancelAsync();
        if (_publisher is not null) await _publisher.DisposeAsync();
        if (_subscriber is not null) await _subscriber.DisposeAsync();
        if (_broker is not null) await _broker.DisposeAsync();
        (_publisher, _subscriber, _broker, _cts, _subscription) = (null, null, null, null, null);
        Status = "";
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
