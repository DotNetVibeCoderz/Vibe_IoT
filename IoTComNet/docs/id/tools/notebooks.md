---
title: Notebook
translation-status: synced
---

# Notebook

Polyglot notebook (.NET Interactive) ada di `notebooks/`, satu per protokol, dalam English (`.en.ipynb`) dan Bahasa
Indonesia (`.id.ipynb`) dengan kode yang identik. Buka di VS Code dengan ekstensi *Polyglot Notebooks*, atau di
Jupyter dengan kernel .NET. Setiap notebook berjalan dengan simulator di dalam proses.

| Notebook | Topik |
|---|---|
| `00-start-here` | model endpoint, link in-memory, traffic tap |
| `industrial/01-modbus` | server, simulator, client, urutan word, mode read-only, mesin Rust, pemecahan masalah |
| `transport/02-framing-crc` | katalog CRC, SLIP/COBS/HDLC, decode streaming |
| `navigation/03-nmea` | penguraian, penerima simulasi, fix GNSS |
| `messaging/04-mqtt-senml` | broker, subscription wildcard, SenML JSON vs CBOR |
| `automotive/06-can-uds` | frame CAN, ISO-TP, UDS, dan OBD-II terhadap simulator ECU |
| `medical/05-hl7-dicom` | bangun/urai ORU^R01, MLLP dengan ACK, DICOM C-STORE studi sintetis |
| `99-protocol-chooser` | protokol mana untuk tugas apa |

Setiap notebook mengikuti struktur yang sama: apa itu protokolnya → persiapan → client → server → pub/sub (bila
relevan) → eksperimen dengan simulator → pemecahan masalah → latihan.

Notebook dihasilkan dari satu spesifikasi (`build/generate_notebooks.py`) sehingga kedua bahasa tidak pernah menyimpang;
CI mengekstrak setiap sel kode dan menjalankannya sebagai program.

Bekerja dari hasil clone? Pack library secara lokal lalu arahkan notebook ke sana:

```
#i "nuget: <repo>/artifacts/packages"
#r "nuget: IoTCom.Net, 0.1.0-preview.1"
```
