---
title: BACnet
translation-status: synced
---

# BACnet/IP

**Ringkasan.** BACnet (ASHRAE 135, ISO 16484-5) adalah protokol sistem manajemen gedung: chiller, air handling unit,
kotak VAV, panel pencahayaan, dan meter. Sebuah *device* memiliki *objek* (Analog Input, Analog Output, Analog Value,
Binary Input/Output/Value, Multi-state Value, …) yang *properti*-nya (Present_Value, Units, Status_Flags, …) dibaca dan
ditulis oleh BMS. BACnet/IP (Annex J) membawanya lewat UDP 47808. IoTCom.Net menyediakan dua paket, sesuai jawaban
desain untuk BACnet — adapter **dan** API server sendiri:

**`IoTCom.Net.Protocols.Bacnet`** (milik sendiri, dapat di-trim dan kompatibel NativeAOT):

- Codec: `BacnetWriter`/`BacnetReader` (tag aplikasi dan konteks, tag pembuka/penutup, panjang diperluas),
  `BacnetPacket` (BVLC termasuk Forwarded-NPDU dan BVLC-Result, NPDU dengan pengalamatan jaringan, setiap tipe APDU)
  dengan frame lane, dan `BacnetServices` untuk Who-Is/I-Am, Who-Has/I-Have, ReadProperty, ReadPropertyMultiple,
  WriteProperty, SubscribeCOV, dan notifikasi COV.
- `BacnetDevice`: device BACnet/IP. Device menjawab Who-Is (rentang diperiksa, I-Am broadcast, unicast opsional),
  Who-Has, ReadProperty (indeks array), ReadPropertyMultiple (ALL/REQUIRED/OPTIONAL dan galat per properti),
  WriteProperty, dan SubscribeCOV (notifikasi confirmed dengan pengulangan atau unconfirmed, lifetime, COV increment),
  menolak layanan yang tidak dikenal, dan membatalkan (abort) permintaan bersegmen serta jawaban yang terlalu panjang.
- `BacnetObject`: input, output, dan value analog, biner, dan multi-state dengan properti wajibnya, priority array 16
  tingkat dan relinquish default untuk objek yang dapat diperintah, status flag out-of-service dan fault, serta
  pemeriksaan tipe data dan jangkauan yang menjadi kode galat standar.
- `BacnetAhuSimulator`: pengendali air handling unit (perintah dan bukti kipas, katup air dingin dan damper udara luar
  pada loop masing-masing kecuali diambil alih, suhu, CO₂, tekanan statis, set point, okupansi, alarm filter).

**`IoTCom.Net.Adapters.Bacnet`** (adapter di atas pustaka BACnet ela-compil, MIT, dipatok 4.0.0):

- `BacnetIpClient`: penemuan Who-Is, ReadProperty, ReadPropertyMultiple, WriteProperty dengan prioritas
  (**hanya-baca secara bawaan**), SubscribeCOV dengan callback (notifikasi confirmed diakui), memakai tipe BACnet milik
  IoTCom. Jembatan transport-nya menjalankan pustaka itu di atas transport datagram IoTCom apa pun, sehingga bekerja di
  jaringan dalam memori maupun UDP sungguhan. Pustaka itu tidak dianotasi untuk trimming, jadi adapter ini tidak dapat
  di-trim, tidak kompatibel AOT, dan tidak ada di meta-package.

Rangkaian uji menjalankan klien adapter terhadap device milik IoTCom sendiri — dua implementasi BACnet independen.
Codec juga diperiksa terhadap `/conformance/bacnet.json`, yang dihasilkan referensi Python independen.

## Kapan dipakai

- Membaca dan memerintah titik BMS dari .NET: dasbor, laporan energi, deteksi gangguan, analitik.
- Mengekspos peralatan atau gateway Anda sendiri sebagai device BACnet yang dapat ditemukan BMS (`BacnetDevice` dengan
  objek Anda).
- Menguji integrasi BMS terhadap air handling unit simulasi.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Bacnet --prerelease   # API server (juga di meta-package IoTCom.Net)
dotnet add package IoTCom.Net.Adapters.Bacnet --prerelease    # klien
```

## Mulai cepat

Device milik Anda sendiri:

```csharp
using IoTCom.Net.Protocols.Bacnet;

await using var device = BacnetDevice.Create(o => { o.DeviceInstance = 1001; o.DeviceName = "Chiller plant gateway"; });
var supply = device.Add(BacnetObject.AnalogInput(1, "CHW supply temperature", BacnetUnits.DegreesCelsius, 6.5));
var enable = device.Add(BacnetObject.BinaryOutput(1, "Chiller enable", relinquishDefault: false));
enable.Changed += (o, _) => Console.WriteLine($"enable → {o.FormatValue()} (priority {o.ActivePriority})");
await device.StartAsync();       // UDP 47808, I-Am broadcast
supply.Update(6.8);              // subscriber mendapat notifikasi COV saat perubahan mencapai COV increment
```

Klien:

```csharp
using IoTCom.Net.Adapters.Bacnet;

await using var bms = BacnetIpClient.Create(o => o.AllowWrites());
await bms.ConnectAsync();
foreach (var d in await bms.DiscoverAsync()) Console.WriteLine(d);
var t = await bms.ReadPresentValueAsync(2604, BacnetObjectId.Parse("AI1"));
await using var cov = await bms.SubscribeCovAsync(2604, BacnetObjectId.Parse("AI1"), n => Console.WriteLine(n.Values[0].Values[0]));
await bms.WritePropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Enumerated(0), priority: 8);
await bms.WritePropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Null, priority: 8);   // relinquish
```

Jika broadcast tidak sampai ke device (subnet lain, kontainer), daftarkan langsung:
`bms.AddDevice(2604, new IPEndPoint(IPAddress.Parse("192.168.1.40"), 47808))`.

## Keamanan

Penulisan menyalakan dan mematikan kipas serta pompa, membuka katup, dan mengubah set point di gedung yang berpenghuni.
Klien menolaknya sampai `AllowWrites()`; CLI membutuhkan `--allow-write` dan bertanya sebelum menulis ke device
sungguhan, dan Gallery punya kunci tulis. Tulislah pada prioritas milik peran Anda (8 untuk operator manual) dan
relinquish setelah selesai — jangan pernah menulis pada 1 atau 2 (keselamatan jiwa). BACnet/IP tidak punya
autentikasi; pisahkan jaringan BMS dan jangan pernah membuka UDP 47808 ke internet. Device yang Anda bangun dengan
`BacnetDevice` dapat menolak semua penulisan dengan `WriteProtected`. Vendor identifier 999 untuk pengujian; produk
membutuhkan identifier yang diberikan ASHRAE.

## Alat

```bash
iotcom bacnet demo [--allow-write]                         # device dan klien dalam proses
iotcom bacnet discover                                     # Who-Is di jaringan Anda (--broadcast 192.168.1.255)
iotcom bacnet objects 2604                                 # objek, nama, present value
iotcom bacnet read 2604 AI1                                # present value; atau properti: priority-array, all
iotcom bacnet write 2604 BO1 inactive --priority 8 --allow-write
iotcom bacnet subscribe 2604 AI1,AI4,BI1                   # notifikasi COV
iotcom bacnet serve --device 2604                          # AHU simulasi di UDP 47808 untuk YABE atau BMS
iotcom bacnet decode 810A00110104000507 0C0C00000001 1955
```

Perintah klien menerima `--sim` (unit dalam proses) dan `--address <ip>` (tanpa penemuan). Rekaman memetakan BACnet ke
UDP 47808 sehingga Wireshark menguraikannya; ekstensi VS Code mengurai `bacnet`. Gallery: *AHU lewat BACnet*.

## Pengujian

Uji codec mencakup object identifier, encoding ReadProperty, priority array (perintah, prioritas lebih rendah
diabaikan, relinquish, nilai di luar jangkauan, properti hanya-baca, input out-of-service), dan frame lane. Ke-45 vektor
bersama mencakup setiap tag aplikasi termasuk string panjang dan bit string, serta datagram BACnet/IP (Who-Is dengan
dan tanpa rentang, I-Am, ReadProperty dan ACK-nya, SimpleACK, Error, Reject, Abort, notifikasi COV, Forwarded-NPDU,
BVLC-Result, SubscribeCOV) ditambah enam datagram rusak.

Uji interop menjalankan adapter (klien ela-compil) terhadap device IoTCom di jaringan dalam memori: penemuan dengan dan
tanpa rentang yang cocok; object list (utuh dan indeks 0); ReadPropertyMultiple dengan nilai, ALL, dan galat per
properti; objek tak dikenal; penolakan mode hanya-baca, perintah prioritas 8, priority array, relinquish, set point,
dan state di luar jangkauan; COV confirmed dan unconfirmed dengan notifikasi awal, COV increment, dan pembatalan;
serta respons unit simulasi terhadap perintah.

## Keterbatasan

Segmentasi tidak didukung (jawaban yang lebih besar dari APDU di-abort, seperti yang diwajibkan standar untuk device
tanpa segmentasi). Tidak ada BBMD atau registrasi foreign device di server, tidak ada routing antarjaringan, tidak ada
alarm dan event, schedule, calendar, trend log, file, atau CreateObject/DeleteObject di API server, dan tidak ada
BACnet/SC atau MS/TP. Adapter klien mendukung apa yang didukung pustakanya; paket ini membuka penemuan, pembacaan,
penulisan, dan COV.

## Pelajari lebih lanjut

Notebook `notebooks/building/26-bacnet.id.ipynb` · sampel `samples/console/BacnetBms` · [KNX](knx.md)
