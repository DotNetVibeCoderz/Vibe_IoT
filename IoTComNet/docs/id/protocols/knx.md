---
title: KNX
translation-status: synced
---

# KNXnet/IP

**Ringkasan.** KNX adalah bus gedung untuk pencahayaan, tirai, pemanas, dan kehadiran. Perangkat di jalur twisted-pair
(TP1) punya individual address (`area.line.device`, misalnya `1.1.4`) dan berkomunikasi lewat group address
(`main/middle/sub`, misalnya `3/0/1`): tombol menulis "on" ke `1/0/1`, dan setiap aktuator yang mendengarkan `1/0/1`
bereaksi. Setiap group address membawa satu datapoint type (DPT). KNXnet/IP membawa jalur itu ke Ethernet (UDP 3671):
*tunnelling* memberi satu klien sambungan titik-ke-titik lewat antarmuka KNX IP, *routing* me-multicast setiap
telegram ke `224.0.23.12`. `IoTCom.Net.Protocols.Knx` menyediakan:

- Codec (sans-I/O): `KnxIndividualAddress`, `KnxGroupAddress` (tiga tingkat, dua tingkat, dan mentah),
  `KnxCemiFrame` (cEMI L_Data.req/.con/.ind dengan field kontrol dan layanan grup), `KnxIp` (header, HPAI, CRI/CRD,
  DIB perangkat dan service family, setiap layanan core, tunnelling, dan routing), frame lane untuk keduanya, dan
  `KnxDpt` untuk DPT 1, 2, 3, 5, 6, 7, 8, 9 (float 2-byte KNX), 10, 11, 12, 13, 14, 16, 17, 18, 20.102, dan 232.600
  beserta nama dan satuannya.
- `KnxTunnelClient`: tersambung (mode NAT secara bawaan), heartbeat setiap 60 dtk dengan tiga kesempatan, penghitung
  urutan, TUNNELING_ACK dengan satu kali ulang, L_Data.con diperiksa (konfirmasi negatif adalah galat), permintaan
  ulang diakui tetapi tidak disampaikan dua kali, `GroupValueReceived` untuk seluruh jalur, `ReadAsync` yang menunggu
  respons pertama. **Hanya-baca secara bawaan.**
- `KnxRouter`: peserta routing di grup multicast, dengan API `IKnxGroupClient` yang sama.
- `KnxDiscovery.SearchAsync`: SEARCH_REQUEST dan jawabannya sebagai `KnxDeviceInfo`.
- `KnxIpGateway`: antarmuka dan router KNX IP dalam perangkat lunak — search dan description, hingga N tunnel dengan
  individual address, pengawasan heartbeat, pengakuan dengan pengulangan (klien yang tidak menjawab diputus),
  L_Data.con, penerusan jalur ke setiap tunnel dan ke grup multicast.
- `KnxBus` (jalur TP1 dalam proses) dan `KnxBuildingSimulator`: lantai kantor dengan aktuator saklar, dimmer, dan
  tirai, pengendali ruangan dengan katup pemanas dan mode HVAC, detektor kehadiran, dan stasiun cuaca, dengan rencana
  group address di `GroupAddresses`.

Sesuai desain, KNX ditulis dalam C# terkelola di atas `System.Net.Sockets` (tanpa kembaran Rust). Codec diperiksa
terhadap `/conformance/knx.json`, yang dihasilkan referensi Python independen.

## Kapan dipakai

- Visualisasi, dasbor, dan pencatatan untuk instalasi KNX (kantor, hotel, sekolah, rumah).
- Menjembatani KNX ke MQTT, OPC UA, atau sistem manajemen gedung.
- Menguji integrasi KNX tanpa instalasi: gateway dan lantai berjalan di mana saja, juga di belakang UDP sungguhan.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Knx --prerelease    # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

Memantau dan membaca instalasi sungguhan:

```csharp
using IoTCom.Net.Protocols.Knx;

await using var knx = KnxTunnelClient.Create(o => o.UseGateway("192.168.1.10"));   // hanya-baca
knx.GroupValueReceived += e => Console.WriteLine(e);
await knx.ConnectAsync();
double temperature = (double)await knx.ReadAsync("3/0/1", "9.001");
```

Menulis, setelah diizinkan:

```csharp
await using var knx = KnxTunnelClient.Create(o => o.UseGateway("192.168.1.10").AllowWrites());
await knx.ConnectAsync();
await knx.WriteAsync("1/0/1", "1.001", true);      // lampu nyala
await knx.WriteAsync("2/1/1", "5.001", 75);        // tirai ke 75 %
await knx.WriteAsync("3/1/1", "9.001", 21.5);      // set point
```

Instalasi tanpa perangkat keras, yang dapat dijangkau ETS atau alat KNXnet/IP apa pun di mesin ini:

```csharp
await using var bus = new KnxBus();
var floor = new KnxBuildingSimulator(bus);
await using var router = KnxIpGateway.Create(o => { o.UseUdp(KnxIp.Port); o.Bus = bus; });
await router.StartAsync();
// panggil floor.Step(detik) secara berkala untuk menggerakkan tirai dan suhu
```

Mencari antarmuka: `await KnxDiscovery.SearchAsync()`. Routing: `KnxRouter.Create()` bergabung ke `224.0.23.12:3671`.

## Keamanan

Penulisan grup menyalakan lampu, menggerakkan tirai dan motor, serta mengubah set point pemanas di gedung yang
berpenghuni. Tunnel dan router menolaknya sampai `AllowWrites()`; CLI membutuhkan `--allow-write` dan bertanya
sebelum menulis ke instalasi sungguhan, dan Gallery punya kunci tulis. KNXnet/IP tidak punya autentikasi: tempatkan
antarmuka di jaringan terpisah dan jangan pernah membuka UDP 3671 ke internet. KNX Secure (IP Secure, Data Secure)
belum diimplementasikan.

## Alat

```bash
iotcom knx demo [--allow-write]                                   # penemuan, tunnel, dan lantai kantor
iotcom knx discover                                               # SEARCH_REQUEST di jaringan lokal
iotcom knx monitor --gateway 192.168.1.10                         # group monitor (hanya-baca); --routing untuk multicast
iotcom knx read 3/0/1 --dpt 9.001 --gateway 192.168.1.10
iotcom knx write 1/0/1 on --dpt 1.001 --gateway 192.168.1.10 --allow-write
iotcom knx decode "29 00 BC E0 11 04 18 01 03 00 80 0C 33" --dpt 9.001
```

Setiap perintah menerima `--sim` untuk memakai router dan lantai dalam proses, bukan instalasi. Rekaman memetakan
KNXnet/IP ke UDP 3671 sehingga Wireshark menguraikannya; ekstensi VS Code mengurai `knxip` dan `cemi`. Gallery:
*Lantai kantor lewat KNX*.

## Pengujian

Uji codec mencakup alamat (tiga tingkat, dua tingkat, mentah, di luar jangkauan), pemformatan DPT dan pemeriksaan
jangkauan, serta frame lane. Ke-56 vektor bersama mencakup setiap DPT yang didukung termasuk batas float KNX
(`0x7FFF` dicadangkan untuk data tidak valid), frame cEMI dengan nilai pendek dan panjang, informasi tambahan dan
frame rusak, serta connect, connection state, tunnelling, routing, dan header rusak KNXnet/IP.

Uji sesi menjalankan router, lantai, dan klien di jaringan UDP dalam memori: pembacaan dan penolakan mode hanya-baca;
penulisan yang mencapai aktuator dengan status yang terlihat oleh tunnel kedua tetapi tidak dipantulkan ke pengirim;
heartbeat, `E_NO_MORE_CONNECTIONS`, dan channel yang bebas setelah diputus; tunnelling request yang diulang diakui dua
kali tetapi diterapkan sekali; klien yang berhenti mengakui diputus; penemuan; dan peserta routing yang menulis dan
membaca.

## Keterbatasan

KNX Secure, KNXnet/IP lewat TCP, manajemen perangkat (pemrograman individual address, akses properti, pembacaan
memori), extended frame, dan long frame belum diimplementasikan. Tunnel memakai link layer (tanpa mode busmonitor).
Kontrol alir routing (ROUTING_BUSY) tidak ditindaklanjuti. Router perangkat lunak hanya berbicara dengan jalurnya
dalam proses, bukan dengan coupler TP1 sungguhan.

## Pelajari lebih lanjut

Notebook `notebooks/building/25-knx.id.ipynb` · sampel `samples/console/KnxMonitor`
