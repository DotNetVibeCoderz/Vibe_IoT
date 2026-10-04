---
title: MQTT
translation-status: synced
---

# MQTT 3.1.1 / 5.0

**Ringkasan.** Protokol publish/subscribe untuk telemetri IoT. IoTCom.Net **tidak** menulis ulang MQTT: ia
mengadaptasi [MQTTnet](https://github.com/dotnet/MQTTnet) ke kontrak IoTCom dan menambahkan hal-hal yang selalu
ditulis ulang oleh aplikasi.

## Kapan dipakai

- Mengirim telemetri dari perangkat edge atau gateway ke broker atau layanan IoT cloud.
- Distribusi ke banyak konsumen; perintah kembali ke perangkat.
- Memisahkan produsen dari konsumen dengan topik dan state yang di-retain.

## Peran

| Peran | Tipe |
|---|---|
| Publisher + subscriber | `MqttEndpoint` |
| Broker (tertanam) | `MqttBroker` |

Yang ditambahkan adapter: subscription `IAsyncEnumerable` yang dihitung referensinya per filter, reconnect otomatis
**dengan resubscribe**, helper string/JSON/SenML, traffic tap, metrik, dan integrasi hosting.

## Instalasi

```bash
dotnet add package IoTCom.Net.Adapters.Mqtt --prerelease
```

## Mulai cepat

```csharp
await using var mqtt = MqttEndpoint.Create(o => o.UseBroker("broker.local").WithClientId("line1-gw"));
await mqtt.ConnectAsync();
await mqtt.PublishStringAsync("plant/line1/state", "running", new PublishOptions { Retain = true });

await foreach (var m in mqtt.SubscribeStringAsync("plant/+/state"))
    Console.WriteLine($"{m.Topic} = {m.Payload}");
```

## Konfigurasi

| Opsi | Default | Deskripsi |
|---|---|---|
| `UseBroker(host, port)` | localhost:1883 | alamat broker |
| `WithTls()` | mati | TLS (gunakan port 8883) |
| `WithCredentials(user, password)` | — | tidak pernah dicatat di log |
| `WithClientId(id)` | acak | id yang stabil memungkinkan sesi persisten |
| `UseMqtt311()` | MQTT 5.0 | versi protokol |
| `WithWill(topic, payload)` | — | last will |
| `WithReconnect(policy)` | backoff | reconnect dan resubscribe |

`PublishOptions`: `QualityOfService` (AtMostOnce / AtLeastOnce / ExactlyOnce), `Retain`, `ContentType` (MQTT 5).

## Contoh

```csharp
// JSON dengan metadata hasil source generator (aman untuk trim/AOT)
await mqtt.PublishJsonAsync("plant/line1/kpi", kpi, MyJsonContext.Default.Kpi);
await foreach (var k in mqtt.SubscribeJsonAsync("plant/+/kpi", MyJsonContext.Default.Kpi)) { /* ... */ }

// Telemetri SenML
var pack = new SenMLPackBuilder("urn:dev:line1:").At(DateTimeOffset.UtcNow).Add("temperature", 23.5, "Cel").Build();
await mqtt.PublishAsync("plant/line1/telemetry", SenMLCodec.ToJson(pack), new PublishOptions { ContentType = SenMLCodec.JsonContentType });

// Broker tertanam untuk pengembangan lokal dan gateway
await using var broker = MqttBroker.Create(1883);
await broker.StartAsync();
```

## Pengujian dan interoperabilitas

Uji integrasi menjalankan publisher dan subscriber terhadap broker tertanam, termasuk routing wildcard. Karena
implementasi di jalur adalah MQTTnet, interoperabilitas dengan Mosquitto, EMQX, HiveMQ, AWS IoT, dan Azure IoT
mengikuti MQTTnet.

## Keamanan

Gunakan TLS dan kredensial di luar lab. Batasi topik dengan ACL broker. Broker tertanam ditujukan untuk edge,
pengujian, dan demo — gunakan broker khusus untuk armada perangkat.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/04-mqtt-senml.id.ipynb` · demo Galeri *Publish & subscribe MQTT* · `iotcom mqtt --help`
