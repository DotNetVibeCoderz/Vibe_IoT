---
title: M-Bus
translation-status: synced
---

# M-Bus (berkabel)

**Ringkasan.** Meter-Bus (EN 13757-2/-3) menghubungkan sub-meter panas, air, gas, dan listrik ke sebuah master lewat
dua kabel, pada 300–9600 baud (biasanya 2400, 8E1). Master menyapa setiap slave lewat alamat primer (1–250) atau
alamat sekunder (ID, pabrikan, versi, medium), lalu membaca **record data variabel**. IoTCom.Net mengimplementasikan:

- **codec frame**: frame E5, short, control, dan long beserta checksum;
- **data variabel** (CI 0x72): identifikasi, pabrikan, medium, access number, status, dan record dengan DIF/DIFE
  (pengkodean data, fungsi, storage, tarif, sub-unit) dan VIF/VIFE (besaran, unit, pangkat sepuluh), BCD, serta
  tanggal tipe F/G;
- **master**: ping SND_NKE, REQ_UD2 dengan frame count bit, pemindaian alamat primer, pemilihan alamat sekunder
  dengan wildcard;
- **simulator slave**: segmen berisi meter panas, meter air, dan meter listrik.

## Kapan digunakan

- Mengumpulkan pembacaan panas, air, dan energi di gedung (sub-metering, alokasi biaya).
- Commissioning segmen: menemukan setiap slave dan memeriksa record-nya.
- Menguji pengumpul data tanpa harus turun ke ruang bawah tanah.

## Peran

| Peran | Tipe |
|---|---|
| Master | `MBusMaster`: `PingAsync`, `ReadAsync`, `ScanAsync`, `SelectAsync`, `ReadSecondaryAsync` |
| Slave | `MBusSlaveSimulator` dengan `MBusSimulatedDevice` (`AddDefaultDevices()` menambahkan tiga meter) |
| Codec | `MBusFrame`, `MBusTelegram`, `MBusRecord`, `MBusRecordWriter`, `MBusVif`, `MBusAnatomy` (sans-I/O) |

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.MBus --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using System.IO.Ports;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transport.Serial;

await using var bus = MBusMaster.Create(o => o.UseSerial("COM4", 2400, Parity.Even));
await bus.ConnectAsync();
foreach (var address in await bus.ScanAsync())
{
    var telegram = await bus.ReadAsync(address);
    Console.WriteLine($"{telegram.SecondaryAddress} {telegram.MediumName}");
    foreach (var record in telegram.Records) Console.WriteLine($"  {record}");   // Volume: 12.565 m³
}

var water = await bus.ReadSecondaryAsync("26200002");   // pilih berdasarkan ID, baca lewat alamat 253
```

## Membaca sebuah record

| Byte | Arti |
|---|---|
| DIF | data field (int8…int64, real32, BCD 2–12 digit, panjang variabel), fungsi (sesaat, maks, min, error), bit storage, bit ekstensi |
| DIFE | bit storage tambahan, tarif, sub-unit |
| VIF | besaran dan unit dengan pangkat sepuluh (energi Wh·10ⁿ⁻³, volume m³·10ⁿ⁻⁶, suhu aliran °C·10ⁿ⁻³, tanggal tipe G, tanggal-waktu tipe F, …) |
| VIFE / `0xFD` | ekstensi seperti error flag, tegangan, dan arus |

`MBusRecord` memberikan besaran, unit, `Value` yang sudah diskalakan, `Time` untuk tanggal, nomor storage, tarif, dan
sub-unit.

## Alat

```bash
iotcom mbus scan --sim --to 10
iotcom mbus scan --serial COM4               # 2400 8E1 lewat level converter
iotcom mbus read 1 -h 10.0.0.40 -p 10001     # lewat gateway M-Bus/TCP
iotcom mbus read 26200002 --sim              # alamat sekunder
iotcom mbus decode 681F1F680802727856341224400107550000000313153100DA023B13018B60043718021816
iotcom mbus simulate --port 10001
dotnet run --project samples/console/MBusScanner
```

## Pengujian dan interoperabilitas

- **Codec:** C# dan crate Rust `iotcom-mbus` menjalankan vektor yang sama (`/conformance/mbus.json`): telegram meter
  air dari dokumentasi M-Bus (checksum 0x18), record dengan storage, tarif, sub-unit, filler, dan data pabrikan, frame
  short dan seleksi, serta frame yang rusak. Codec Rust di-fuzz (`cargo fuzz run mbus`).
- **Sesi:** pemindaian, pembacaan dengan access number yang bertambah, alamat sekunder dengan wildcard, wildcard
  ambigu yang akan bertabrakan di bus sungguhan, dan slave yang tidak ada.

## Keamanan

M-Bus berkabel tidak punya keamanan: siapa pun di segmen bisa membacanya. Enkripsi (mode 5/7) milik wireless M-Bus
dan OMS, yang belum dicakup. Membaca tidak pernah mengubah meter; pustaka ini hanya mengirim SND_UD untuk memilih
slave.

## Keterbatasan

Belum tersedia: wireless M-Bus (EN 13757-4) dan enkripsi OMS, penulisan alamat primer dan perintah SND_UD lainnya,
alarm REQ_UD1, pembacaan multi-telegram (DIF 0x1F dilaporkan), penggantian baud rate, dan tabel VIFE yang lengkap
(ekstensi yang dapat digabung disimpan sebagai byte mentah).

## Pelajari lebih lanjut

Demo Galeri *Pembacaan smart meter* · notebook `notebooks/metering/10-dlms-mbus.id.ipynb` · [DLMS/COSEM](dlms.md) ·
`iotcom mbus --help`
