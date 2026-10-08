---
title: Galeri IoTCom.Net
translation-status: synced
---

# Galeri IoTCom.Net

Aplikasi desktop (Avalonia — Windows, Linux, macOS) untuk belajar sambil menjalankan: pilih use case, mulai, baca
kodenya, dan amati byte-nya. Setiap demo berbicara dengan simulator di dalam proses, jadi tidak perlu perangkat keras.

```bash
dotnet run --project gallery/IoTCom.Net.Gallery
```

![Galeri — pabrik pintar](../../images/gallery-modbus.png)

## Setiap demo punya empat tab

| Tab | Menampilkan |
|---|---|
| **Jalankan** | panel interaktif |
| **Kode** | source demo — file yang sama persis yang dikompilasi ke aplikasi, dengan syntax highlight |
| **Dokumentasi** | penjelasan singkat dalam English atau Bahasa Indonesia, dengan halaman docs terkait |
| **Lalu lintas** | Protocol Inspector: setiap frame sebagai *frame lane* berwarna, hex dump frame yang dipilih, dan **Simpan .pcapng** untuk Wireshark |

Status bar menampilkan lampu run, status demo, dan frame terakhir di jalur.

![Tab lalu lintas](../../images/gallery-traffic.png)

## Demo

| Kategori | Demo | Protokol |
|---|---|---|
| Industri | PLC pabrik pintar — baca PLC virtual, nyalakan motor, geser setpoint; beralih antara mesin C# dan **Rust** | Modbus TCP |
| Otomotif | Diagnostik kendaraan — scan tool dan simulator ECU mesin di bus CAN virtual: takometer dan lampu indikator, data langsung OBD-II, VIN, DTC, security access, dan penulisan yang dijaga | CAN, ISO-TP (Rust), UDS, OBD-II |
| Medis & kesehatan | Monitor pasien ICU — 4 bed sintetis mengirim ORU^R01 lewat MLLP; papan bangsal, kurva langsung, NEWS2, tren, dan catatan SBAR dari LLM | HL7 v2, MLLP |
| Medis & kesehatan | Pra-baca gambar dengan AI — modalitas CT/MR/X-ray simulasi menyimpan ke PACS; viewer dengan window dan pra-baca model vision yang dinilai terhadap temuan yang ditanam | DICOM C-STORE |
| Navigasi & maritim | Telemetri drone lewat MAVLink — artificial horizon, jejak terbang, baterai, pesan status; arm, lepas landas, RTL, dan mendarat dengan COMMAND_ACK (dijaga) | MAVLink 2 |
| Navigasi & maritim | Pelacak kendaraan GNSS — lintasan langsung, kecepatan, kekuatan sinyal satelit | NMEA 0183 |
| Gedung pintar & panggung | Rumah kaca pintar lewat CoAP — sensor yang diamati, aktuator, log Block2, separate response; slider kehilangan paket dan diagram urutan pesan langsung menampilkan pengiriman ulang | CoAP, Observe, Block-wise, SenML |
| Gedung pintar & panggung | Lampu panggung lewat Art-Net — fader, master, chase; fixture menampilkan apa yang diurai penerima | Art-Net 4, DMX512 |
| Pesan | Publish & subscribe MQTT — broker tertanam, sensor SenML, subscription wildcard | MQTT 5, SenML |
| Meja kerja protokol | Meja kerja frame & checksum — urai frame Modbus per field, 23 CRC, SLIP/COBS/HDLC langsung | Modbus, CRC, framing |

![Diagnostik kendaraan](../../images/gallery-can-uds.png)
![Monitor pasien ICU](../../images/gallery-hl7-icu.png)
![Pra-baca gambar dengan AI](../../images/gallery-dicom-ai.png)

Demo medis memakai penyedia AI bila sudah dikonfigurasi (lihat [panduan AI medis](../guides/medical-ai.md)); bila belum, teks berbasis aturan dipakai.

![Drone MAVLink](../../images/gallery-mavlink.png)
![NMEA](../../images/gallery-nmea.png)
![Rumah kaca CoAP](../../images/gallery-coap.png)
![Pencahayaan](../../images/gallery-lighting.png)
![Meja kerja](../../images/gallery-workbench.png)

## Bahasa dan tema

Tombol **BAHASA / ENGLISH** di rail mengganti setiap label, teks demo, dan baris status saat runtime; **GELAP /
TERANG** mengganti tema.

![Gelap, Bahasa Indonesia](../../images/gallery-dark-id.png)

## Menambah demo

Turunkan dari `GalleryDemo` (logika di `XxxDemo.cs`, tampilan di `XxxDemo.View.cs`) lalu tambahkan ke daftar di
`MainViewModel`. Implementasikan `OnStartAsync`/`OnStopAsync`, pasang `Tap` ke endpoint Anda, dan sediakan teks
`Title`, `Summary`, dan `Docs` dalam kedua bahasa.

## Screenshot

`gallery/IoTCom.Net.Gallery.Screenshots` merender jendela asli di luar layar dengan Avalonia.Headless dan Skia:

```bash
dotnet run --project gallery/IoTCom.Net.Gallery.Screenshots -- docs/images
```
