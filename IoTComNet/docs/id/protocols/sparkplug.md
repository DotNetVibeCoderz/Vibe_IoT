---
title: Sparkplug B
translation-status: synced
---

# Sparkplug B (MQTT yang sadar state untuk industri)

**Ringkasan.** MQTT biasa tidak mengatakan apa isi sebuah topik atau apakah penerbitnya masih hidup. Sparkplug B
(Eclipse Foundation) memperbaiki keduanya: namespace topik tetap `spBv1.0/{group}/{type}/{edge node}[/{device}]`,
payload Protobuf dengan metrik bertipe, dan model sesi yang dibangun di atas pesan will MQTT. IoTCom.Net
mengimplementasikannya di atas adapter MQTTnet:

- `SparkplugPayload` / `SparkplugMetric` — payload Protobuf (nomor field Tahu) untuk setiap tipe skalar, dengan
  `Describe` untuk frame lane; `SparkplugTopic` mengurai dan membangun topik, termasuk `spBv1.0/STATE/{host}`;
- `SparkplugEdgeNode` — mendaftarkan NDEATH (dengan `bdSeq`) sebagai will MQTT, menerbitkan NBIRTH dan satu DBIRTH
  per perangkat dengan setiap metrik beserta alias, melaporkan perubahan sebagai NDATA/DDATA dengan nomor urut 0–255,
  menjawab `Node Control/Rebirth`, menerapkan penulisan ke metrik yang writable dari NCMD/DCMD (atau mengabaikannya
  dengan `AcceptWrites = false`), dan mengirim DDEATH/NDEATH saat dihentikan;
- `SparkplugHost` — host application: STATE online (retained, dengan will offline), mengikuti birth dan death,
  menerjemahkan alias, mendeteksi lompatan nomor urut dan data dari node yang belum terlihat birth-nya, meminta rebirth
  sekali, dan mengirim penulisan bertipe berdasarkan birth certificate;
- `SparkplugLineSimulator` — lini pembotolan (filler dan tangki sirup) untuk demo dan pengujian.

## Kapan dipakai

- Menerbitkan data pabrik ke Ignition, HiveMQ, EMQX, Cirrus Link, atau Unified Namespace apa pun yang berbicara Sparkplug.
- Membangun konsumen ringan di sisi SCADA yang perlu tahu nilai mana yang terkini dan mana yang basi.
- Menguji gateway edge terhadap host tanpa sistem SCADA sungguhan.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Sparkplug --prerelease   # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Sparkplug;

await using var node = SparkplugEdgeNode.Create(o =>
{
    o.Group = "Plant"; o.EdgeNode = "Line1";
    o.Mqtt = m => m.UseBroker("broker.local").WithCredentials("edge", "secret");
});
node.Device("Filler")
    .Metric("Speed", SparkplugDataType.Float, 0f)
    .Metric("Running", SparkplugDataType.Boolean, true, writable: true);
node.CommandReceived += (_, c) => Console.WriteLine($"write {c.Device}/{c.Metric} = {c.Value}");
await node.StartAsync();                                       // NBIRTH, DBIRTH
await node.Devices["Filler"].SetAsync("Speed", 118.5f);         // DDATA, lewat alias

await using var host = SparkplugHost.Create(o => { o.HostId = "scada"; o.Mqtt = m => m.UseBroker("broker.local"); });
host.MetricUpdated += (_, e) => Console.WriteLine($"{e.View.Key} {e.Metric}");
host.StateChanged += (_, v) => Console.WriteLine($"{v.Key} {(v.Online ? "online" : "offline")}");
await host.StartAsync();
await host.WriteAsync("Plant", "Line1", "Filler", "Running", false);   // DCMD
```

Dengan hosting: `services.AddIoTCom(b => b.AddSparkplugEdgeNode(o => …, node => node.Device("Filler").Metric(…)))` dan
`AddSparkplugHost(o => …)` mendaftarkan singleton yang mulai dan berhenti bersama aplikasi.

## Keamanan

Penulisan mengubah peralatan yang sedang berjalan. Hanya metrik yang dideklarasikan `writable` yang menerima NCMD/DCMD;
`AcceptWrites = false` membuat edge node mengabaikan setiap penulisan namun tetap melayani permintaan rebirth. CLI
mewajibkan `--allow-write` dan konfirmasi untuk `iotcom sparkplug write`.

## Alat

```bash
iotcom sparkplug watch --sim                     # broker tertanam + lini simulasi, log pesan langsung
iotcom sparkplug watch --host broker.local       # ikuti namespace sungguhan sebagai host "iotcom-cli"
iotcom sparkplug simulate --embedded-broker      # edge node yang bisa dipantau pihak lain
iotcom sparkplug write Plant/Line1/Filler Running false --allow-write
iotcom payload sparkplug <hex>                   # urai payload hasil tangkapan
```

Gallery: *Jaringan pabrik · Sparkplug B* (cabut kabel jaringan dan lihat will NDEATH tiba). Ekstensi VS Code mengurai
payload `sparkplug` di frame viewer.

## Pengujian

Uji payload memeriksa byte terhadap payload Tahu yang dienkode manual dan round-trip setiap tipe skalar, bilangan
bulat negatif, dan null; uji topik mencakup semua tipe pesan dan STATE. Uji sesi berjalan terhadap broker tertanam:
birth, data lewat alias, penulisan DCMD, metrik yang tidak writable, will NDEATH setelah koneksi putus dengan `bdSeq`,
host yang bergabung terlambat dan meminta rebirth, serta edge node hanya-baca.

## Keterbatasan

Data set, template, properti metrik, dan metadata dilewati saat decode dan tidak dihasilkan saat encode. Buffer data
historis saat offline belum disertakan. STATE host Sparkplug 3.0 (JSON) dipakai; STATE teks biasa versi 2.2 tidak.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/12-mdns-sparkplug.id.ipynb` · sampel `samples/console/SparkplugEdgeNode` · [MQTT](mqtt.md)
