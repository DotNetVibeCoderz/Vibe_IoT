---
title: Framing dan CRC
translation-status: synced
---

# Framing dan CRC

**Ringkasan.** Bagian kecil tanpa state yang dibutuhkan setiap link embedded: batas frame (SLIP, COBS, HDLC, baris)
dan deteksi kerusakan (CRC, LRC). Tanpa alokasi, streaming, dan diverifikasi dengan test vector bersama.

## Kapan dipakai

- Berkomunikasi dengan mikrokontroler lewat UART/USB dengan format pesan sendiri.
- Mengimplementasikan atau men-debug protokol yang menetapkan CRC (Modbus, DNP3, MAVLink, HDLC, M-Bus, BLE, RTCM…).
- Memecah protokol teks (NMEA, perintah AT, ASTM) menjadi baris.

## Katalog CRC

`CrcAlgorithm` adalah mesin berbasis tabel untuk lebar 8–64 bit dengan parameter model Rocksoft. `CrcCatalog` berisi
23 preset; masing-masing lolos nilai check katalog untuk ASCII `123456789`.

| Lebar | Preset |
|---|---|
| 8 | CRC-8/SMBUS, CRC-8/MAXIM-DOW (1-Wire), CRC-8/SAE-J1850 |
| 16 | ARC, MODBUS, IBM-3740 (CCITT-FALSE), XMODEM, KERMIT, IBM-SDLC (X.25/HDLC), MCRF4XX (MAVLink), DNP, EN-13757 (wM-Bus), USB, MAXIM-DOW |
| 24 | OPENPGP, LTE-A (CRC-24Q, RTCM 3), BLE |
| 32 | ISO-HDLC (CRC-32), ISCSI (CRC-32C), MPEG-2 (STM32), BZIP2 |
| 64 | XZ, ECMA-182 |

```csharp
ulong crc = CrcCatalog.Crc16Modbus.Compute(frame);
var mavlink = CrcCatalog.Find("mavlink")!;            // alias: modbus, x25, crc32c, rtcm, ...
var custom = new CrcAlgorithm(new CrcParameters("MY-CRC", 16, 0x1021, 0x1D0F, false, false, 0, 0xE5CC));
ushort crc16 = Crc16.Modbus(frame);                    // helper ushort
byte lrc = Lrc.Compute(body);                          // Modbus ASCII
```

## Codec framing

| Codec | Delimiter | Overhead | Catatan |
|---|---|---|---|
| `Slip` (RFC 1055) | `0xC0` | hingga 2× | END di awal opsional |
| `Cobs` | `0x00` | ≤ 1 byte per 254 | tidak ada byte nol di dalam frame |
| `Hdlc` | `0x7E` | escape `0x7D`, FCS-16 ditambahkan | gaya async HDLC / PPP |
| `LineFraming` | `\n` (membuang `\r`) | tidak ada | NMEA, AT, ASTM |

Semua mengimplementasikan `IFrameEncoder` dan `IFrameDecoder` streaming, yang bekerja pada `ReadOnlySequence<byte>`
langsung dari `PipeReader` (frame yang terpotong di beberapa read tetap tertangani, frame tidak valid dilewati dan
stream pulih).

```csharp
await pipe.Writer.WriteFrameAsync(new Cobs(), payload);
await foreach (var frame in transport.Pipe.Input.ReadFramesAsync(new Cobs())) Handle(frame);
```

## Pengujian

`/conformance/crc.json` (115 vector), `cobs.json` (contoh COBS kanonik), dan `slip.json` dihasilkan oleh referensi
bit-per-bit yang independen (`conformance/generate.py`) dan dijalankan oleh test suite C# dan Rust.

## Pelajari lebih lanjut

Notebook `notebooks/transport/02-framing-crc.id.ipynb` · Galeri *Meja kerja frame & checksum* · `iotcom crc --all`, `iotcom frame --help`
