---
title: CoAP
translation-status: synced
---

# CoAP

**Ringkasan.** Constrained Application Protocol (RFC 7252) memberi perangkat bertenaga baterai dan mikrokontroler
antarmuka bergaya web di atas UDP: resource dengan path, GET/PUT/POST/DELETE, kode respons, dan content format, dalam
datagram berukuran beberapa byte. IoTCom.Net mengimplementasikan client dan server dengan:

- pesan confirmable, pengiriman ulang, dan deduplikasi;
- separate response;
- **Observe** (RFC 7641), **Block-wise transfer** (RFC 7959), dan **penemuan resource** (RFC 6690);
- payload SenML.

Tersedia juga node rumah kaca simulasi.

![Rumah kaca pintar lewat CoAP](../../images/gallery-coap.png)

## Kapan dipakai

- Berbicara dengan perangkat yang memakai CoAP: Zephyr, RIOT, Contiki-NG, perangkat OpenThread/Thread, klien LwM2M,
  serta banyak modul NB-IoT dan LTE-M.
- Membangun perangkat atau gateway yang menyediakan antarmuka REST ringan di jaringan terbatas.
- Mendapat pembaruan dorong (Observe) dari sensor tanpa polling atau broker.

## Peran

| Peran | Tipe |
|---|---|
| Client / subscriber | `CoapClient`: `GetAsync`, `PutAsync`, `PostAsync`, `DeleteAsync`, `ObserveAsync` (`IAsyncEnumerable`), `DiscoverAsync`, `PingAsync` |
| Server | `CoapServer`: `Map(path, get, put, post, delete, observable…)`, `resource.NotifyAsync()` |
| Codec | `CoapMessage` (`Encode`/`TryDecode`), `CoapBlock`, `CoapLinkFormat`, `CoapAnatomy` (sans-I/O) |
| Simulator | `CoapDeviceSimulator`: node rumah kaca dengan sensor yang dapat diamati, aktuator, log Block2, unggah firmware Block1, dan routine lambat |

## Transport

CoAP berjalan di atas datagram: `UseUdp(port)` (bawaan 5683) atau `UseInMemory(network)` pada `InMemoryDatagramNetwork`.
Jaringan di memori punya pengaturan `LossRate` dan `DuplicateRate` untuk menguji keandalan. DTLS (coaps, port 5684)
direncanakan (Fase 2).

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Coap --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Coap;

await using var coap = CoapClient.Create(o => o.UseServer("192.168.1.40"));
await coap.ConnectAsync();

var temp = await coap.GetAsync("/sensors/temperature");
Console.WriteLine($"{temp.Code}: {temp.PayloadText}");          // 2.05 Content: 27.5

await coap.PutAsync("/actuators/fan", "on");                      // 2.04 Changed

await foreach (var n in coap.ObserveAsync("/sensors/temperature"))
    Console.WriteLine($"{n.PayloadText} (seq {n.ObserveSequence})");
```

## Menyediakan resource

```csharp
await using var server = CoapServer.Create(o => o.UseUdp(5683));
double level = 41;
var soil = server.Map("/sensors/soil",
    get: (req, ct) => ValueTask.FromResult(CoapReply.Content(level.ToString("0.0"))),
    observable: true, resourceType: "soil-moisture");
server.Map("/actuators/valve",
    put: (req, ct) => { /* req.PayloadText */ return ValueTask.FromResult(CoapReply.Changed()); });
await server.StartAsync();

level = 43.5;
await soil.NotifyAsync();     // mendorong nilai baru ke setiap observer
```

Server menangani detail protokol untuk Anda:

- `/.well-known/core` dibuat dari resource yang terdaftar, dengan penyaringan seperti `?rt=temperature*`.
- Option kritis yang tidak dikenal ditolak dengan 4.02, dan method yang tidak didukung dengan 4.05.
- Handler yang lebih lambat dari `SeparateResponseAfter` (800 ms) di-ACK lebih dulu, lalu dijawab belakangan sebagai
  separate response.
- Isi yang besar dilayani dalam blok Block2, dan unggahan Block1 dirakit ulang sebelum handler berjalan.

## Cara kerja keandalan

| Mekanisme | Perilaku |
|---|---|
| Confirmable (CON) | dikirim ulang setelah `AckTimeout × [1, 1,5]`, berlipat ganda, hingga `MaxRetransmit` (4) kali |
| Non-confirmable (NON) | dikirim sekali (request dengan `Confirmable = false`; sebagian besar notifikasi) |
| Deduplikasi | message ID yang diterima diingat selama `DeduplicationLifetime` (247 dtk); duplikat mendapat jawaban yang disimpan |
| Separate response | ACK kosong lebih dulu, lalu respons CON yang di-ACK oleh client |
| Token | mencocokkan respons dan notifikasi dengan request; token tak dikenal dijawab RST, yang mengakhiri observasi |
| Observe | server mengirim notifikasi NON, setiap notifikasi ke-5 berupa CON agar observer yang mati terdeteksi; client membuang notifikasi usang (RFC 7641 §3.4) dan membatalkan registrasi saat enumerasi dihentikan |

`client.Statistics` dan `server.Statistics` menghitung pengiriman ulang, duplikat, reset, dan timeout.

## Konfigurasi

| Opsi | Bawaan | Keterangan |
|---|---|---|
| `Transmission.AckTimeout` | 2 dtk | ACK_TIMEOUT |
| `Transmission.MaxRetransmit` | 4 | MAX_RETRANSMIT |
| `Transmission.BlockSize` | 1024 | ukuran blok yang diutamakan (16–1024) untuk Block1/Block2 |
| `Transmission.ResponseTimeout` | 30 dtk | waktu tunggu separate response atau respons NON |
| `Confirmable` (client) | true | request CON atau NON |
| `ReadOnly` (client) | false | memblokir PUT, POST, dan DELETE |
| `SeparateResponseAfter` (server) | 800 ms | kapan beralih ke separate response |

## Content format

`CoapContentFormat` mendaftar format yang terdaftar: text/plain (0), link-format (40), JSON (50), CBOR (60),
SenML JSON (110), dan SenML CBOR (112). Simulator menjawab sensornya dalam SenML bila diminta; urai payload-nya dengan
`SenMLCodec`:

```csharp
var r = await coap.GetAsync("/sensors/humidity", accept: CoapContentFormat.SenMLJson);
var record = SenMLCodec.Resolve(SenMLCodec.ParseJson(r.Payload.Span)).Single();
```

## Alat

```bash
iotcom coap serve --port 5683 --frames                      # node rumah kaca simulasi
iotcom coap discover coap://127.0.0.1/ --query "rt=temperature*"
iotcom coap get coap://127.0.0.1/sensors/temperature --accept senml
iotcom coap observe coap://127.0.0.1/sensors/soil
iotcom coap put coap://127.0.0.1/actuators/fan on --allow-write
iotcom coap ping coap://127.0.0.1/
dotnet run --project samples/console/CoapObserve
```

## Pengujian dan interoperabilitas

- **Codec:** codec C# dan crate Rust `iotcom-coap` menjalankan vektor yang sama (`/conformance/coap.json`). Vektor ini
  dihasilkan oleh encoder ketiga yang independen dan mencakup contoh RFC 7252 serta pesan yang rusak. Codec Rust
  di-fuzz (`cargo fuzz run coap`).
- **Endpoint:** pengujian mencakup GET/PUT, filter penemuan, SenML, unduhan Block2 dan unggahan Block1, separate
  response, Observe dengan pembatalan registrasi, mode read-only, UDP sungguhan, serta jaringan dengan kehilangan
  paket 20–30% plus duplikasi, di mana setiap request harus sampai ke handler tepat satu kali.

## Keamanan

CoAP polos tidak diautentikasi dan tidak dienkripsi. Gunakan di jaringan terisolasi atau di balik VPN sampai dukungan
DTLS/OSCORE tersedia. Perlakukan request sebagai masukan tak tepercaya, dan buat client read-only bila hanya memantau.

## Keterbatasan

Belum tersedia: DTLS (coaps), OSCORE, CoAP lewat TCP/WebSocket (RFC 8323), request multicast, proxy, validasi ETag dan
penanganan If-Match di server, serta FETCH/PATCH (RFC 8132). LwM2M di atas tumpukan ini direncanakan untuk Fase 2.

## Pelajari lebih lanjut

Demo Galeri *Rumah kaca pintar lewat CoAP* · notebook `notebooks/messaging/07-coap.id.ipynb` · [SenML](senml.md) ·
`iotcom coap --help`
