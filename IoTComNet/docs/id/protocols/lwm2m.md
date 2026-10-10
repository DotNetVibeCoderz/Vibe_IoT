---
title: LwM2M
translation-status: synced
---

# OMA LwM2M

**Ringkasan.** Lightweight M2M (LwM2M) adalah standar manajemen perangkat untuk perangkat terbatas dan seluler (NB-IoT,
LTE-M). Ia berjalan di atas CoAP. Perangkat, yaitu klien LwM2M, mendaftar ke server, menjaga registrasinya tetap
hidup, dan membuka keadaannya sebagai objek bernomor dari registry OMA: Device (3), Location (6), Temperature (3303),
Light Control (3311), dan ratusan lainnya. Server membaca, menulis, mengeksekusi, dan meng-observe-nya lewat path,
misalnya `/3311/0/5851` untuk dimmer. `IoTCom.Net.Protocols.Lwm2m` dibangun di atas stack CoAP milik IoTCom.Net
sendiri dan menyediakan:

- `Lwm2mClient`, sisi perangkat:
  - registrasi (`POST /rd` dengan nama endpoint, lifetime, binding, dan tautan objek), pembaruan otomatis pada 80 %
    lifetime dengan registrasi ulang bila server melupakan perangkat, dan pembatalan registrasi
  - Read, Discover, Write (ganti dan pembaruan sebagian), Execute, dan Observe pada instance objek (`Lwm2mInstance`
    dengan `Set`, validator `OnWrite`, dan handler `OnExecute`)
  - Write-Attributes `pmin`/`pmax` yang mengatur notifikasi, pembatalan observasi, dan `AbortAsync` untuk menyimulasikan
    perangkat yang hilang dari jaringan
- `Lwm2mServer`, sisi manajemen:
  - registrasi dengan pembaruan dan kedaluwarsa (`Registered`, `Updated`, `Deregistered` beserta alasannya)
  - `ReadAsync`, `DiscoverAsync`, `WriteAsync`, `ExecuteAsync`, `WriteAttributesAsync`, dan `ObserveAsync`
  - hanya-baca secara bawaan: Write dan Execute memerlukan `AllowWrites()`
- Format konten `Lwm2mContent`: TLV (11542), teks biasa, opaque, SenML JSON (110), dan SenML CBOR (112), dengan tipe
  dari `Lwm2mRegistry`; frame lane TLV (`DescribeTlv`).
- `Lwm2mStreetLightSimulator`: lampu jalan dengan Device (termasuk Reboot), Location, suhu driver LED, dan Light Control
  (dimmer diperiksa 0–100 %).

Sesuai desain, LwM2M ditulis dalam C# di atas CoAP IoTCom.Net (tanpa kembaran Rust). Codec TLV diperiksa dengan
`/conformance/lwm2m.json`, yang dihasilkan oleh referensi Python independen.

## Kapan dipakai

- Mengelola armada perangkat NB-IoT, LTE-M, atau perangkat terbatas lain: inventaris, konfigurasi, reboot, telemetri.
- Membangun perangkat yang harus bekerja dengan platform LwM2M yang sudah ada (Eclipse Leshan, AVSystem Coiote,
  platform manajemen perangkat operator).
- Menguji server terhadap perangkat simulasi, atau perangkat terhadap server berskrip.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Lwm2m --prerelease    # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

Sebuah perangkat:

```csharp
using IoTCom.Net.Protocols.Lwm2m;

await using var client = Lwm2mClient.Create(o => { o.EndpointName = "urn:dev:sensor:42"; o.UseServer("lwm2m.example.com"); });
client.AddInstance(3).Set(0, "Acme").Set(1, "TH-1").Set(2, "42").OnExecute(4, _ => { /* reboot */ return true; });
var temperature = client.AddInstance(3303).Set(5700, 21.5).Set(5701, "Cel");
await client.ConnectAsync();          // mendaftar
temperature.Set(5700, 22.0);          // observer diberi notifikasi
```

Sebuah server:

```csharp
await using var server = Lwm2mServer.Create(o => o.UseUdp(5683));
server.Registered += r => Console.WriteLine(r);
await server.StartAsync();
// kemudian
var device = await server.ReadAsync("urn:dev:sensor:42", Lwm2mPath.Parse("/3/0"));
await server.WriteAttributesAsync("urn:dev:sensor:42", Lwm2mPath.Parse("/3303/0/5700"), pmin: 10, pmax: 300);
await using var watch = await server.ObserveAsync("urn:dev:sensor:42", Lwm2mPath.Parse("/3303/0/5700"), v => Console.WriteLine(v[0]));
```

## Keamanan

Write dan Execute mengubah apa yang dilakukan perangkat; resource Reboot dan firmware membuatnya offline. Server
menolaknya sampai `Lwm2mServerOptions.AllowWrites()`. Di sisi perangkat, validator `OnWrite` menolak nilai yang tidak
boleh diterima resource (dimmer simulator menolak 150 %), dan resource tanpa akses Write menjawab 4.05. Paket ini
berjalan di atas CoAP biasa di UDP 5683: penerapan produksi memakai DTLS (port 5684) dengan pre-shared key atau
sertifikat, yang merupakan item terpisah di roadmap. Jangan membuka server tanpa perlindungan ke internet.

## Alat

```bash
iotcom lwm2m demo                                   # server dan lampu jalan dalam proses
iotcom lwm2m demo --allow-write
iotcom lwm2m serve --observe /3303/0/5700           # server hanya-baca di UDP 5683
iotcom lwm2m client --server 127.0.0.1              # lampu jalan ke server LwM2M mana pun
iotcom lwm2m decode 3/0/6 8606410001410105          # TLV
```

Di Git Bash, tulis path tanpa garis miring di depan (`3/0/6`) atau setel `MSYS_NO_PATHCONV=1`: shell mengubah
`/3/0/6` menjadi path Windows. Ekstensi VS Code mengurai TLV (`lwm2m-tlv`); tangkapan memakai UDP 5683 sehingga
Wireshark menampilkan pertukaran CoAP. Gallery: *Lampu jalan lewat LwM2M*.

## Pengujian

Uji codec mencakup:
- path, penyarangan, dan path yang tidak valid;
- contoh Device dari spesifikasi LwM2M, diurai dan disandikan ulang byte demi byte, juga dibungkus dalam instance
  objek;
- ukuran integer minimum, float atau double, boolean, waktu, object link, dan nilai unsigned, dengan panjang yang
  salah ditolak;
- perjalanan pulang-pergi SenML JSON dan CBOR, nilai teks biasa, serta TLV dan SenML yang rusak.

Uji sesi menjalankan server dan lampu jalan lewat jaringan dalam memori:
- registrasi dengan tautan objek, serta pembacaan dalam TLV, teks biasa, SenML JSON, dan SenML CBOR;
- discover, objek yang tidak ada (4.04), dan membaca resource yang bisa dieksekusi (4.05);
- penolakan mode hanya-baca, lalu penulisan (ganti, pembaruan sebagian, SenML), nilai dimmer yang ditolak perangkat
  (4.00), penulisan ke resource hanya-baca (4.05), dan Execute;
- observasi dengan pmin/pmax: notifikasi saat berubah dan notifikasi lain setelah pmax tanpa perubahan, lalu
  pembatalan;
- pembaruan pada 80 % lifetime, pembatalan registrasi, dan kedaluwarsa perangkat yang hilang dari jaringan.

Ke-19 vektor TLV bersama (termasuk enam payload rusak) dijalankan di suite C# dan disandikan ulang byte demi byte.

## Keterbatasan

Belum ada DTLS, server bootstrap (`/bs`), Create atau Delete instance objek, operasi Send, mode antrean atau binding
SMS, maupun format LwM2M JSON (11543). Nilai besar tidak dipecah dengan transfer block-wise CoAP oleh lapisan ini.
Notifikasi memakai atribut `pmin` dan `pmax`; `gt`, `lt`, dan `st` belum dievaluasi.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/21-lwm2m.id.ipynb` · sampel `samples/console/Lwm2mClient` · [CoAP](coap.md) ·
[SenML](senml.md)
