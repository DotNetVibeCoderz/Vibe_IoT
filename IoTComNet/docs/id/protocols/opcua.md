---
title: OPC UA
translation-status: synced
---

# OPC UA (adapter di atas stack OPC Foundation)

**Ringkasan.** OPC UA (IEC 62541) adalah cara PLC, SCADA, MES, dan historian menerbitkan datanya: address space berisi
objek, variabel, dan method, dicapai lewat sesi dengan sertifikat aplikasi, enkripsi, dan identitas pengguna. Sesuai
aturan kurasi desain, IoTCom.Net **tidak** menulis ulang OPC UA. `IoTCom.Net.Adapters.OpcUa` membungkus stack
OPC Foundation .NET Standard (`OPCFoundation.NetStandard.Opc.Ua.Client`/`.Server` 1.5.378, MIT) dan menambahkan:

- `OpcUaClient` — endpoint client IoTCom: sambung (endpoint paling aman secara default, atau SecurityPolicy None;
  anonim atau nama pengguna), `BrowseAsync`, `ReadAsync` (satu atau banyak), `WriteAsync` (nilai dikonversi ke tipe
  variabel), `CallAsync`, dan `SubscribeAsync` yang mengembalikan `IAsyncEnumerable<OpcUaValue>`; saklar `ReadOnly`
  yang menolak penulisan dan pemanggilan method; panggilan layanan di traffic tap;
- `OpcUaPlantServer` — simulator dengan lini pembotolan (filler, tangki sirup, alarm level tinggi, method
  `ResetCounter`) yang menawarkan None dan Basic256Sha256 Sign / SignAndEncrypt, untuk pengujian, demo, dan
  pengembangan client.

## Kapan dipakai

- Membaca dan menulis tag PLC atau server SCADA dari layanan .NET, gateway, dan pengujian.
- Menjembatani OPC UA ke MQTT / Sparkplug B atau basis data bersama bagian lain IoTCom.Net.
- Mengembangkan client OPC UA tanpa peralatan pabrik, terhadap simulator.

## Instalasi

```bash
dotnet add package IoTCom.Net.Adapters.OpcUa --prerelease
```

Adapter ini bukan bagian dari meta-package IoTCom.Net: stack OPC Foundation besar dan tidak aman untuk trimming/AOT.

## Mulai cepat

```csharp
using IoTCom.Net.Adapters.OpcUa;

await using var ua = OpcUaClient.Create(o =>
{
    o.UseEndpoint("opc.tcp://plc.local:4840");
    o.WithCredentials("operator", Environment.GetEnvironmentVariable("PLC_PASSWORD")!);
    o.ReadOnly = true;                                       // tidak ada kode di bawah yang bisa mengubah perangkat
});
await ua.ConnectAsync();                                     // Basic256Sha256 / SignAndEncrypt bila ditawarkan

foreach (var node in await ua.BrowseAsync()) Console.WriteLine(node);              // anak dari Objects
var speed = await ua.ReadAsync("ns=2;s=Plant/Line1/Filler/Speed");
Console.WriteLine($"{speed.Text} {speed.Status} {speed.SourceTimestamp}");

await foreach (var change in ua.SubscribeAsync(["ns=2;s=Plant/Line1/Tank7/Level"], TimeSpan.FromMilliseconds(500)))
    Console.WriteLine(change);
```

## Sertifikat dan trust

Setiap aplikasi OPC UA punya sertifikatnya sendiri. Saat pertama tersambung, client membuat sertifikat self-signed di
direktori PKI-nya (`%LOCALAPPDATA%/IoTCom.Net/opcua/pki` secara default, atau `PkiPath`), dengan store `own`,
`trusted`, `issuer`, dan `rejected`:

1. Sambungkan sekali. Jika sertifikat server belum dikenal, koneksi gagal dan sertifikatnya ditulis ke `rejected/`.
   Periksa, lalu pindahkan ke `trusted/certs/`.
2. Server biasanya menolak sertifikat client dengan cara yang sama: percayai di alat konfigurasi server.
3. `AcceptUntrustedCertificates = true` (CLI `--accept-untrusted`) melewati langkah 1 — hanya untuk commissioning dan
   pengujian.

## Keamanan

`ReadOnly = true` membuat `WriteAsync` dan `CallAsync` melempar `ReadOnlyModeException` sebelum apa pun sampai ke
server. CLI membuka sesi hanya-baca untuk `browse`, `read`, dan `watch`; `write` dan `call` perlu `--allow-write` (dan
konfirmasi untuk `write` ke server sungguhan). Access level di sisi server tetap berlaku: menulis variabel hanya-baca
gagal dengan `DeviceException` (BadNotWritable).

## Alat

```bash
iotcom opcua simulate --port 4840                         # simulator pabrik untuk alat lain (UaExpert, Node-RED…)
iotcom opcua browse --sim                                 # simulator di dalam proses, pohon dengan nilai
iotcom opcua browse -e opc.tcp://plc.local:4840 --accept-untrusted
iotcom opcua read --sim Line1/Filler/Speed Line1/Tank7/Level
iotcom opcua watch --sim Line1/Tank7/Level -i 250
iotcom opcua write --sim Line1/Filler/Setpoint 100 --allow-write
iotcom opcua call --sim Line1 Line1/ResetCounter --allow-write
```

Dengan `--sim`, node id bisa ditulis relatif terhadap pabrik (`Line1/Filler/Speed`). Gallery: *Penjelajah tag OPC UA*.

## Pengujian

Pengujian menjalankan simulator dan client di dalam proses: menjelajah pabrik, membaca nilai dan node yang tidak ada
(BadNodeIdUnknown), menulis dengan konversi tipe, menolak penulisan ke variabel hanya-baca, pemanggilan method, mode
read-only, subscription yang mengirim perubahan, sesi Basic256Sha256 SignAndEncrypt dengan sertifikat yang dibuat saat
itu juga, dan endpoint yang tidak terjangkau dilaporkan sebagai `TransportException`.

## Keterbatasan

Event serta alarms & conditions, historical access, tipe kompleks (struktur), dan autentikasi pengguna di sisi server
belum dibungkus; pakai `OpcUaClient.Session` untuk apa pun yang tidak dicakup adapter. Simulator hanya menerima
pengguna anonim.

## Pelajari lebih lanjut

Notebook `notebooks/industrial/13-opcua.id.ipynb` · sampel `samples/console/OpcUaBrowser` · [Sparkplug B](sparkplug.md)
