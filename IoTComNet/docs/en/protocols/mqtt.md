---
title: MQTT
translation-status: synced
---

# MQTT 3.1.1 / 5.0

**Summary.** The publish/subscribe protocol of IoT telemetry. IoTCom.Net does **not** re-implement MQTT: it adapts
[MQTTnet](https://github.com/dotnet/MQTTnet) to the IoTCom contracts and adds what applications keep rewriting.

## When to use it

- Sending telemetry from edge devices or gateways to a broker or cloud IoT service.
- Fan-out to many consumers; commands back to devices.
- Decoupling producers from consumers with topics and retained state.

## Roles

| Role | Type |
|---|---|
| Publisher + subscriber | `MqttEndpoint` |
| Broker (embedded) | `MqttBroker` |

What the adapter adds: `IAsyncEnumerable` subscriptions reference-counted per filter, automatic reconnect **with
resubscribe**, string/JSON/SenML helpers, the traffic tap, metrics, and hosting integration.

## Installation

```bash
dotnet add package IoTCom.Net.Adapters.Mqtt --prerelease
```

## Quickstart

```csharp
await using var mqtt = MqttEndpoint.Create(o => o.UseBroker("broker.local").WithClientId("line1-gw"));
await mqtt.ConnectAsync();
await mqtt.PublishStringAsync("plant/line1/state", "running", new PublishOptions { Retain = true });

await foreach (var m in mqtt.SubscribeStringAsync("plant/+/state"))
    Console.WriteLine($"{m.Topic} = {m.Payload}");
```

## Configuration

| Option | Default | Description |
|---|---|---|
| `UseBroker(host, port)` | localhost:1883 | broker address |
| `WithTls()` | off | TLS (use port 8883) |
| `WithCredentials(user, password)` | — | never logged |
| `WithClientId(id)` | random | stable ids allow persistent sessions |
| `UseMqtt311()` | MQTT 5.0 | protocol version |
| `WithWill(topic, payload)` | — | last will |
| `WithReconnect(policy)` | backoff | reconnect and resubscribe |

`PublishOptions`: `QualityOfService` (AtMostOnce / AtLeastOnce / ExactlyOnce), `Retain`, `ContentType` (MQTT 5).

## Examples

```csharp
// JSON with source-generated metadata (trim/AOT safe)
await mqtt.PublishJsonAsync("plant/line1/kpi", kpi, MyJsonContext.Default.Kpi);
await foreach (var k in mqtt.SubscribeJsonAsync("plant/+/kpi", MyJsonContext.Default.Kpi)) { /* ... */ }

// SenML telemetry
var pack = new SenMLPackBuilder("urn:dev:line1:").At(DateTimeOffset.UtcNow).Add("temperature", 23.5, "Cel").Build();
await mqtt.PublishAsync("plant/line1/telemetry", SenMLCodec.ToJson(pack), new PublishOptions { ContentType = SenMLCodec.JsonContentType });

// Embedded broker for local development and gateways
await using var broker = MqttBroker.Create(1883);
await broker.StartAsync();
```

## Testing and interoperability

Integration tests run publisher and subscriber against the embedded broker, including wildcard routing. Because the
wire implementation is MQTTnet, interoperability with Mosquitto, EMQX, HiveMQ, AWS IoT and Azure IoT follows MQTTnet.

## Security

Use TLS and credentials outside the lab. Scope topics with broker ACLs. The embedded broker is meant for the edge,
tests and demos — use a dedicated broker for fleets.

## Learn more

Notebook `notebooks/messaging/04-mqtt-senml.en.ipynb` · Gallery demo *MQTT publish & subscribe* · `iotcom mqtt --help`
