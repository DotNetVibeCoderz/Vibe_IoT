---
title: CLI iotcom
translation-status: synced
---

# CLI `iotcom`

```bash
dotnet tool install -g IoTCom.Net.Cli --prerelease
iotcom --help
```

![iotcom](../../images/cli.png)

## Perintah

| Perintah | Fungsi |
|---|---|
| `iotcom info` | versi, kredit, daftar protokol |
| `iotcom ports` | daftar port serial |
| `iotcom crc <hex> [--algorithm nama] [--all] [--text ...]` | menghitung CRC (23 preset, alias seperti `modbus`, `x25`, `mavlink`) |
| `iotcom frame encode|decode slip|cobs|hdlc <hex>` | codec framing |
| `iotcom modbus read` | membaca coil, discrete input, input atau holding register (`--watch`, `--float`) |
| `iotcom modbus write` | menulis coil atau register — wajib `--allow-write` dan konfirmasi |
| `iotcom modbus serve` | slave Modbus; `--simulate` menjalankan PLC virtual; mencatat setiap request |
| `iotcom modbus decode <frame>` | frame lane per field untuk frame TCP, RTU, atau ASCII |
| `iotcom nmea listen` | panel fix GNSS langsung (`--raw` mencetak kalimat) |
| `iotcom nmea simulate` | GPS lewat NMEA-over-TCP |
| `iotcom can list` | antarmuka SocketCAN dan port serial (adapter slcan) |
| `iotcom can dump` / `send` | candump/cansend untuk backend apa pun (`--can socketcan:can0`, `slcan:COM5`, `sim`) |
| `iotcom can simulate` | simulator ECU mesin di balik adapter slcan tiruan lewat TCP |
| `iotcom uds read` / `dtc` / `raw` | identifikasi UDS, DTC (`--clear --allow-write`), request mentah dengan frame lane |
| `iotcom obd live` / `vin` / `dtc` | data langsung OBD-II (`--watch`), VIN, DTC tersimpan dan tertunda |
| `iotcom hl7 listen` | penerima MLLP dengan ACK otomatis dan observasi terurai (`--raw` mencetak segmen) |
| `iotcom hl7 send` | kirim berkas ER7 (atau contoh ORU^R01) dan tampilkan ACK |
| `iotcom hl7 simulate` | monitor pasien sintetis (`--scenario sepsis\|hypoxia\|hypertension\|stable`) |
| `iotcom dicom listen` | DICOM Storage SCP (`--output` menyimpan .dcm + pratinjau PNG) |
| `iotcom dicom send` | C-STORE berkas atau studi sintetis (`--synthetic ct\|mr\|xray --finding …`) |
| `iotcom dicom echo` | verifikasi C-ECHO |
| `iotcom artnet send|poll|monitor` | kirim DMX, temukan node, pantau universe |
| `iotcom mqtt pub|sub|broker` | publish, subscribe, menjalankan broker |

## Opsi koneksi (Modbus)

`--host`, `--port` (TCP) · `--serial COM3 --baud 19200 --parity even` (RTU) · `--rtu`, `--ascii` · `--unit` ·
`--timeout` · `--native` (gunakan mesin Rust).

## Resep

```bash
# PLC virtual di satu terminal…
iotcom modbus serve --port 1502 --simulate
# …dan tampilan langsung di terminal lain
iotcom modbus read --port 1502 --table input --count 8 --float --watch 1000

# Apa isi frame dari logic analyzer ini?
iotcom modbus decode "01 03 00 00 00 0A C5 CD" --mode rtu

# CRC mana yang dipakai perangkat saya?
iotcom crc "01 03 00 00 00 0A" --all

# Pantau universe Art-Net
iotcom artnet monitor --universe 0
```

Kode keluar: `0` sukses, `1` error, `2` frame tidak valid / tidak ada frame, `3` penulisan tidak diizinkan, `4`
dibatalkan saat konfirmasi. Atur `IOTCOM_FORCE_ANSI=1` agar warna tetap ada saat output dialihkan.
