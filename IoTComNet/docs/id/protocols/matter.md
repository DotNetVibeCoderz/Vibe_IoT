---
title: Matter
translation-status: synced
---

# Matter (controller, perangkat, dan bridge)

**Ringkasan.** Matter adalah standar rumah pintar dari Connectivity Standards Alliance: perangkat IPv6 yang
di-commission sekali oleh controller — membuktikan ia tahu setup passcode, memeriksa atestasi perangkat, dan memberinya
sertifikat operasional — lalu dibaca, ditulis, diperintah, dan dilanggani lewat sesi terenkripsi.
`IoTCom.Net.Protocols.Matter` mengimplementasikan kedua sisi dalam C# terkelola:

- **Codec** (sans-I/O): `Tlv`/`TlvElement` (semua tipe elemen dan bentuk tag), `MatterSetupPayload` (QR `MT:` base38 dan
  kode manual 11/21 digit dengan digit cek Verhoeff), `MatterMessage` (header pesan dan protokol, nonce dan enkripsi
  AES-CCM), `StatusReport`, `InteractionModel` (pesan Read, Write, Invoke, Subscribe, Report, dan Status, path atribut dan
  perintah dengan wildcard), `MatterCertificate` (sertifikat operasional TLV Matter beserta DER X.509 persis yang
  ditandatangani, penerbitan, CSR, compressed fabric id, dan IPK).
- **Keamanan**: `Spake2Plus` (PASE: SPAKE2+ di atas P-256), CASE (SIGMA dengan sertifikat operasional), kunci sesi,
  penghitung pesan dengan deteksi duplikat, keandalan MRP (acknowledgement dan pengiriman ulang), atestasi perangkat
  (`MatterAttestation`: rantai DAC → PAI → PAA dan tanda tangan).
- **`MatterController`**: sebuah fabric (root CA, NOC-nya sendiri, IPK; opsional disimpan di `FabricFile`), commissioning
  — PASE, fail-safe, atestasi, CSR, root dan NOC, CASE, CommissioningComplete — dengan progres per langkah, lalu
  `MatterNode`: `ReadAsync` (wildcard), `WriteAttributeAsync`, `InvokeAsync`, `SubscribeAsync`. **Hanya-baca secara
  bawaan**: commissioning, penulisan, dan perintah membutuhkan `AllowWrites()`.
- **`MatterDevice`**: endpoint 0 (Descriptor, Basic Information, General Commissioning, Operational Credentials, Access
  Control), endpoint dan cluster Anda (`MatterCluster`, siap pakai On/Off, Level Control, Temperature Measurement,
  Bridged Device Basic Information), responder PASE dan CASE, tabel fabric, server Interaction Model dengan subscription,
  roll-back fail-safe, dan pemeriksaan akses khusus admin.
- **`MatterBridge`** (Aggregator plus endpoint yang dijembatani) dan **`MatterBridgeSimulator`** (sebuah rumah: empat
  lampu, dua sensor suhu, satu lampu meja yang bisa diredupkan).
- **Penemuan**: `MatterDiscovery` mengiklankan dan menelusuri `_matterc._udp` (commissionable) dan `_matter._tcp`
  (operasional) lewat mDNS IoTCom.

Codec-nya punya kembaran Rust yang di-fuzz, `iotcom-matter`, dan `/conformance/matter.json` bersama (41 vektor) berasal
dari referensi Python independen yang juga menghitung pertukaran SPAKE2+ lengkap dan contoh compressed fabric id dari
spesifikasi.

## Kapan dipakai

- Membangun bridge Matter untuk perangkat di protokol lain (KNX, Modbus, bus proprietary) di .NET.
- Alat uji: commission, baca, dan kendalikan perangkat dari skrip, CI, atau bangku lab; perangkat yang bisa diskrip untuk
  menguji controller.
- Mempelajari Matter dari ujung ke ujung dengan setiap pesan terlihat (traffic tap, frame lane).

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Matter --prerelease    # paket terpisah: tidak termasuk meta-package
```

## Mulai cepat

Controller yang meng-commission perangkat dari kode manualnya lalu menyalakan lampu:

```csharp
using IoTCom.Net.Protocols.Matter;

await using var controller = MatterController.Create(o => o.UseUdp().AllowWrites().TrustTestDevices().FabricFile = "fabric.json");
var node = await controller.CommissionAsync(new IPEndPoint(IPAddress.Parse("192.168.1.40"), 5540),
    MatterSetupPayload.FromManualCode("34970112332"), nodeId: 1);
await node.InvokeAsync(1, MatterIds.OnOff, 1);                                   // On
var onOff = await node.ReadAttributeAsync(1, MatterIds.OnOff, 0);
await node.SubscribeAsync([new AttributePath(null, MatterIds.OnOff, 0)], 0, 60, r => Console.WriteLine($"{r.Path} = {r.Value}"));
```

Perangkat dengan satu lampu yang bisa diredupkan:

```csharp
await using var device = MatterDevice.Create(o => o.UseUdp(5540));
var ep = device.AddEndpoint(new MatterEndpoint(1, (MatterIds.DimmableLight, 3)));
var onOff = ep.Add(new MatterClusters.OnOffCluster());
ep.Add(new MatterClusters.LevelControlCluster(128, onOff));
await device.StartAsync();
Console.WriteLine(device.SetupPayload.ToManualCode());
```

Bridge untuk perangkat di protokol lain:

```csharp
var bridge = new MatterBridge(device);
var (endpoint, kitchen, info) = bridge.AddLight("Kitchen", "knx-1/1/2");
kitchen.Changed += (_, _) => knx.WriteAsync("1/1/2", kitchen.On);               // teruskan perintah Matter
info.Reachable = false;                                                          // perangkat yang dijembatani hilang
```

Hosting: `AddMatterController` dan `AddMatterDevice` (di paket ini).

## Keselamatan

Commissioning menambahkan sebuah fabric — seorang administrator — ke perangkat; penulisan dan perintah mengubah rumah.
Controller hanya-baca sampai `AllowWrites()`; CLI membutuhkan `--allow-write`; Gallery punya kunci tulis. File fabric
menyimpan kunci privat root CA: siapa pun yang memilikinya bisa mengelola setiap perangkat di fabric itu. Jauhkan dari
source control dan buat hanya bisa dibaca oleh Anda. Atestasi uji (`MatterAttestation.TestPaa`, `TrustTestDevices()`)
sengaja dipublikasikan agar perangkat simulasi berfungsi lintas proses — jangan pernah memakainya untuk produk sungguhan.

## Status interoperabilitas

Sudah diverifikasi: contoh onboarding yang dipublikasikan SDK CHIP (`MT:Y.K9042C00KA0648G00`, `34970112332`), contoh
compressed fabric id dari spesifikasi, implementasi Python independen untuk SPAKE2+, TLV, payload, dan header, serta
controller dan perangkat IoTCom lintas proses lewat UDP sungguhan. **Belum diverifikasi terhadap stack Matter lain**
(chip-tool dari SDK CHIP, Apple Home, Google Home, Home Assistant, SmartThings); anggap interoperabilitas masih
eksperimental sampai itu dilakukan. Menjual produk Matter membutuhkan sertifikasi CSA dan rantai atestasi produksi (DAC
dari PAA yang disetujui dan Certification Declaration yang ditandatangani CSA); Certification Declaration tidak
diverifikasi oleh controller ini.

## Alat

```bash
iotcom matter code 34970112332                         # urai kode QR atau manual (atau buat satu)
iotcom matter device [--bridge] [--mdns]               # lampu atau bridge simulasi di UDP 5540
iotcom matter discover
iotcom matter commission 192.168.1.40 34970112332 --node 1 --allow-write [--trust-test]
iotcom matter read 192.168.1.40 1 0/0x0028/*
iotcom matter onoff 192.168.1.40 1 1 toggle --allow-write
iotcom matter subscribe 192.168.1.40 1 */0x0006/0
iotcom matter demo --allow-write
iotcom matter decode <hex>
```

CLI menyimpan fabric-nya di `%APPDATA%/IoTCom.Net/matter/fabric.json` (atau `--fabric`). Capture memetakan Matter ke
UDP 5540; ekstensi VS Code mengurai `matter` (pesan) dan `matter-tlv`. Gallery: *Rumah pintar lewat Matter*.

## Pengujian

Tes codec: kode QR dan manual dari SDK, kode tidak valid, setiap tipe TLV, SPAKE2+ dengan passcode benar dan salah,
AES-CCM dan ECDSA, sertifikat operasional (round trip TLV, DER yang dimuat .NET sebagai X.509 dan tanda tangannya
diverifikasi kunci root). 41 vektor bersama mencakup TLV (termasuk masukan rusak), kode QR dan manual, header pesan,
compressed fabric id, dan dua run SPAKE2+ lengkap; kembaran Rust di-fuzz (`matter`, sekitar 4 juta run lokal). Tes
ujung ke ujung meng-commission lampu dan bridge di memori serta memeriksa pembacaan dengan wildcard, perintah, error
batasan, penulisan, subscription (laporan awal dan perubahan), DNS-SD sebelum dan sesudah commissioning, file fabric
setelah restart, passcode salah, perangkat tak tepercaya, controller hanya-baca, dan fabric asing.

## Batasan

Hanya commissioning di jaringan (tanpa provisioning jaringan BLE, Wi-Fi, atau Thread), tanpa grup atau kunci grup,
tanpa event, tanpa perangkat ICD/sleepy, tanpa OTA, Access Control hanya-baca dengan entri admin, tanpa interaksi
timed, tanpa laporan bertahap (chunked), tanpa resumption sesi, satu fabric per instance controller. Hanya cluster yang
disebut di atas yang tersedia bawaan.

## Pelajari lebih lanjut

Notebook `notebooks/building/31-matter.id.ipynb` · sampel `samples/console/MatterHome` · [mDNS](mdns.md) ·
[DTLS](dtls.md)
