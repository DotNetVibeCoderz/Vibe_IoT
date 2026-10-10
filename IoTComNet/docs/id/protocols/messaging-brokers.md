---
title: Broker pesan (NATS, AMQP 1.0, Kafka)
translation-status: synced
---

# Broker pesan: NATS, AMQP 1.0, dan Kafka

**Ringkasan.** Pabrik dan armada kendaraan sering sudah menjalankan broker pesan, dan gateway harus mengisinya. Ketiga
adapter ini mengikuti aturan kurasi IoTCom.Net: pustaka klien yang sudah matang dibungkus, bukan ditulis ulang, sehingga
setiap protokol menjadi satu `IClientEndpoint` lagi dengan `IPublisher<ReadOnlyMemory<byte>>`,
`ISubscriber<ReadOnlyMemory<byte>>`, traffic tap, metrik (`MessagesPublished`, `MessagesReceived`), dan helper hosting.
Memindahkan gateway dari MQTT ke salah satunya mengganti endpoint, bukan kode di sekelilingnya.

- `IoTCom.Net.Adapters.Nats`: `NatsEndpoint` di atas NATS.Client.Core 3.3.0.
  - subject dan wildcard (`plant.*.temp`, `plant.>`), header, dan content type
  - `ReceiveAsync` dengan queue group opsional (setiap pesan hanya sampai ke satu anggota)
  - `RequestAsync` dan `ServeAsync` untuk request/reply, dengan `DeviceException` bila tak ada yang mendengarkan dan
    `IoTComTimeoutException` bila jawaban terlambat
  - nama pengguna dan kata sandi, token, atau berkas `.creds`
- `IoTCom.Net.Adapters.Amqp`: `AmqpEndpoint` di atas AMQPNetLite.Core 2.5.4 (AMQP 1.0, standar OASIS).
  - publish ke sebuah address; `AtMostOnce` mengirim dalam keadaan pre-settled, `AtLeastOnce` dan `ExactlyOnce`
    menunggu hasil dari broker (`DeviceException` bila ditolak)
  - subscription adalah receiver link; pesan diterima (accepted) setelah konsumen selesai memprosesnya dan lanjut ke
    berikutnya, sehingga konsumen yang berhenti di tengah jalan membiarkan pesan itu dikirim ulang
  - content type di properti pesan, kendali alur berbasis kredit (`ReceiverCredit`)
  - `AmqpMiniBroker`: broker kecil dalam proses (antrean dalam memori, competing consumers, pengiriman ulang,
    kredensial SASL PLAIN opsional) untuk pengujian, notebook, dan demo. Ia bukan untuk produksi dan tidak menyimpan
    apa pun.
- `IoTCom.Net.Adapters.Kafka`: `KafkaEndpoint` di atas Confluent.Kafka 2.16.0 (librdkafka).
  - QoS dipetakan ke pengaturan producer: `AtMostOnce` adalah acks 0, `AtLeastOnce` acks 1, `ExactlyOnce` acks all
    dengan idempotent producer
  - `PublishAsync` dengan key dan header; `ReceiveAsync` mengembalikan topic, key, value, partition, offset, dan header
  - berlangganan satu topic, atau semua topic yang cocok dengan ekspresi reguler bila filter diawali `^`
  - consumer group, offset awal (bawaan earliest), offset di-commit setelah tiap record diserahkan (at-least-once),
    serta opsi TLS dan SASL

Hosting: `AddNats`, `AddAmqp`, dan `AddKafka` mendaftarkan endpoint yang tersambung saat host dimulai. Semuanya ada di
paket adapter masing-masing, jadi tak satu pun masuk meta-package `IoTCom.Net`. Adapter Kafka tidak dapat di-trim dan
tidak kompatibel AOT (seperti OPC UA).

## Kapan dipakai

- Gateway yang membaca perangkat Modbus, CAN, atau BLE lalu mempublikasikannya ke broker NATS, Kafka, atau AMQP yang
  sudah ada di pabrik.
- Request/reply dengan layanan edge lewat NATS, tanpa menulis protokol sendiri.
- Fan-out dan pemutaran ulang telemetri lewat topic Kafka dengan consumer group.
- Menguji kode AMQP di dalam proses dengan `AmqpMiniBroker`, tanpa memasang broker.

Untuk perangkat kecil dan tautan terbatas, MQTT atau CoAP biasanya lebih cocok; untuk data peer-to-peer di jaringan tanpa
broker lihat [Zenoh](zenoh.md).

## Instalasi

```bash
dotnet add package IoTCom.Net.Adapters.Nats --prerelease
dotnet add package IoTCom.Net.Adapters.Amqp --prerelease
dotnet add package IoTCom.Net.Adapters.Kafka --prerelease
```

## Mulai cepat

NATS:

```csharp
using IoTCom.Net.Adapters.Nats;

await using var nats = NatsEndpoint.Create(o => o.UseServer("nats://localhost:4222"));
await nats.ConnectAsync();
await nats.PublishAsync("plant.line1.temp", "21.5"u8.ToArray());

await foreach (var m in nats.ReceiveAsync("plant.>", queueGroup: "loggers", ct))
    Console.WriteLine($"{m.Subject}: {Encoding.UTF8.GetString(m.Data.Span)}");

var reply = await nats.RequestAsync("plant.line1.info", ReadOnlyMemory<byte>.Empty);   // butuh responder
```

AMQP 1.0, dengan broker dalam proses:

```csharp
using IoTCom.Net.Adapters.Amqp;

await using var broker = AmqpMiniBroker.Create();
await broker.StartAsync();
await using var amqp = AmqpEndpoint.Create(o => o.UseBroker(broker.Address));
await amqp.ConnectAsync();
await amqp.PublishAsync("plant.temp", "21.5"u8.ToArray(), new PublishOptions { QualityOfService = QualityOfService.AtLeastOnce });
await foreach (var m in amqp.SubscribeAsync("plant.temp", ct)) { /* ... */ }
```

Kafka:

```csharp
using IoTCom.Net.Adapters.Kafka;

await using var kafka = KafkaEndpoint.Create(o => o.UseBootstrap("localhost:9092").WithGroup("gateway"));
await kafka.ConnectAsync();
await kafka.PublishAsync("plant-temp", key: "line1"u8.ToArray(), value: "21.5"u8.ToArray());

await foreach (var r in kafka.ReceiveAsync("plant-temp", ct))
    Console.WriteLine($"{r.Topic}[{r.Partition}]@{r.Offset} {Encoding.UTF8.GetString(r.Value.Span)}");
```

## Keamanan

Nama pengguna, kata sandi, token, berkas `.creds`, kredensial SASL, dan kata sandi kunci TLS adalah rahasia: simpan di
konfigurasi atau secret store, jangan di kode sumber atau log. Broker di jaringan pabrik sebaiknya mewajibkan
autentikasi. Untuk keamanan transport:

- NATS: URL dan kredensial diteruskan ke NATS.Client.Core; gunakan skema URL TLS-nya bila server Anda mewajibkan TLS.
- AMQP: address diteruskan ke AMQPNetLite; gunakan address `amqps://` untuk TLS. `AmqpMiniBroker` memakai TCP polos dan
  hanya untuk pengujian lokal.
- Kafka: `WithTls(caLocation)` dan `WithSasl(mechanism, user, password, tls: true)` menyetel protokol keamanan
  librdkafka; `Extra` meneruskan pengaturan librdkafka lainnya.

Mempublikasikan ke broker dapat memerintah peralatan sungguhan lewat apa pun yang mengonsumsi topic itu. Perlakukan
subject, address, dan topic yang menggerakkan aktuator seperti akses tulis ke PLC: batasi di broker.

## Alat

Belum ada perintah CLI untuk broker-broker ini. Gunakan `AmqpMiniBroker` dalam proses untuk demo, dan notebook untuk
tur langsung.

## Pengujian

Pengujian AMQP berjalan terhadap `AmqpMiniBroker` di setiap build dan mencakup:
- publish terkonfirmasi (at-least-once dan exactly-once) maupun pre-settled yang bolak-balik utuh, serta subscription
  yang dimulai sebelum publish;
- content type di properti pesan;
- competing consumers yang berbagi satu antrean tanpa duplikat, dan address yang merupakan antrean terpisah;
- pesan yang belum diproses dikirim ulang ke subscriber berikutnya;
- pesan yang ditolak melempar `DeviceException` bila terkonfirmasi tetapi tidak bila pre-settled;
- port tertutup menghasilkan `TransportException`, dan kredensial diperiksa oleh broker;
- dispose mengakhiri subscription yang berjalan (dan idempoten), traffic tap, serta nilai bawaan opsi.

Pengujian NATS dan Kafka butuh broker sungguhan dan dilewati bila tidak ada. Setel `IOTCOM_NATS_URL` (misalnya
`nats://localhost:4222`) dan `IOTCOM_KAFKA_BOOTSTRAP` (misalnya `localhost:9092`) untuk menjalankannya; job CI `brokers`
menyalakan keduanya sebagai service container. Pengujian NATS mencakup publish/subscribe, wildcard, queue group,
header, request/reply, tanpa responder, responder yang lambat, dan dispose. Pengujian Kafka mencakup key, header, dan
content type, ketiga level QoS, subscription ekspresi reguler, offset yang sudah di-commit tidak dikirim ulang ke grup
yang sama, dan dispose. Tanpa broker, hanya nilai bawaan opsi dan jalur kegagalan (broker tak terjangkau, subject atau
topic kosong) yang berjalan.

## Keterbatasan

- Payload berupa byte (gunakan payload codec seperti SenML atau Protobuf untuk struktur); tidak ada dukungan schema
  registry untuk Kafka.
- NATS: hanya NATS inti, tanpa API JetStream (persistensi, replay). Kafka: hanya producer dan consumer, tanpa API admin
  atau transaksi; topic dibuat sesuai kebutuhan bila broker mengizinkan. AMQP: hanya sender dan receiver link pada
  address bernama; tanpa transaksi atau dynamic node.
- `AmqpMiniBroker` adalah test double, bukan broker untuk dideploy.
- Adapter Kafka tidak dapat di-trim dan tidak kompatibel AOT; tak satu pun dari ketiganya ada di meta-package.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/23-brokers.id.ipynb` · [Zenoh](zenoh.md) · [Endpoint dan transport](../concepts/endpoints-and-transports.md)
