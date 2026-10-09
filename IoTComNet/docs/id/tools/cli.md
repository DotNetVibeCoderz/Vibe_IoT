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
| `iotcom mavlink listen` / `simulate` | log MAVLink atau tabel laju/telemetri langsung (`--stats`); quadcopter simulasi lewat UDP |
| `iotcom mavlink cmd` / `params` / `decode` | arm, takeoff, land, rtl, dan penulisan parameter (`--allow-write`); frame lane sebuah frame |
| `iotcom sniff tcp` / `udp` | relay transparan ke perangkat (Modbus/TCP, HL7, CoAP, MAVLink, raw), kedua arah diurai, `--pcap` untuk Wireshark, `--lanes` untuk frame lane |
| `iotcom sniff can` | capture CAN pasif ke konsol dan pcapng (link type SocketCAN) |
| `iotcom nmea listen` | panel fix GNSS langsung (`--raw` mencetak kalimat) |
| `iotcom nmea simulate` | GPS lewat NMEA-over-TCP |
| `iotcom can list` | antarmuka SocketCAN dan port serial (adapter slcan) |
| `iotcom can dump` / `send` | candump/cansend untuk backend apa pun (`--can socketcan:can0`, `slcan:COM5`, `sim`) |
| `iotcom can simulate` | simulator ECU mesin di balik adapter slcan tiruan lewat TCP |
| `iotcom uds read` / `dtc` / `raw` | identifikasi UDS, DTC (`--clear --allow-write`), request mentah dengan frame lane |
| `iotcom obd live` / `vin` / `dtc` | data langsung OBD-II (`--watch`), VIN, DTC tersimpan dan tertunda |
| `iotcom coap get` / `put` / `observe` / `discover` / `ping` | client CoAP dengan URI `coap://host/path` (`--accept senml`, `--frames`; penulisan butuh `--allow-write`) |
| `iotcom coap serve` | node rumah kaca simulasi di UDP 5683 |
| `iotcom lorawan server` | network server LoRaWAN ringan untuk gateway Semtech UDP (`--devices`, `--sim` untuk gateway dan sensor simulasi, `--frames`, `--pcap`) |
| `iotcom lorawan simulate` | gateway dan sensor simulasi terhadap network server mana pun (`--server host:1700`; key disimpan ke file perangkat) |
| `iotcom dlms read` / `objects` / `profile` | meter DLMS/COSEM lewat HDLC (`--serial`), wrapper (`-h`, TCP 4059), atau `--sim`; `--password` atau `--hls` untuk management client |
| `iotcom dlms relay on\|off` / `simulate` | disconnect control (butuh `--allow-write` dan konfirmasi); meter simulasi di TCP |
| `iotcom mbus scan` / `read` / `decode` / `simulate` | master M-Bus (`--serial` 2400 8E1, `-h` gateway, `--sim`): pemindaian primer, pembacaan primer atau sekunder, penguraian telegram, segmen simulasi |
| `iotcom lorawan decode` / `airtime` | urai PHYPayload (hex atau base64; MIC dan dekripsi dengan `--appkey` atau `--nwkskey`/`--appskey`), tabel waktu di udara |
| `iotcom nmea ais decode` / `watch` | urai kalimat !AIVDM; tabel kapal langsung dari `--sim`, `--udp <port>`, atau feed TCP |
| `iotcom at send` / `info` / `sms` / `simulate` | modem perintah AT lewat `--serial`, TCP, atau `--sim`; perintah yang mengubah dan SMS butuh `--allow-write` |
| `iotcom astm listen` / `send` | penerima ASTM E1394 (LIS) dengan keluaran `--hl7`; hasil analyzer sintetis |
| `iotcom hl7 listen` | penerima MLLP dengan ACK otomatis dan observasi terurai (`--raw` mencetak segmen) |
| `iotcom hl7 send` | kirim berkas ER7 (atau contoh ORU^R01) dan tampilkan ACK |
| `iotcom hl7 simulate` | monitor pasien sintetis (`--scenario sepsis\|hypoxia\|hypertension\|stable`) |
| `iotcom dicom listen` | DICOM Storage SCP (`--output` menyimpan .dcm + pratinjau PNG) |
| `iotcom dicom send` | C-STORE berkas atau studi sintetis (`--synthetic ct\|mr\|xray --finding …`) |
| `iotcom dicom echo` | verifikasi C-ECHO |
| `iotcom artnet send|poll|monitor` | kirim DMX, temukan node, pantau universe |
| `iotcom mqtt pub|sub|broker` | publish, subscribe, menjalankan broker |
| `iotcom ble scan` / `services` / `watch` / `write` | central Bluetooth LE di radio sungguhan atau `--sim`: advertisement (iBeacon/Eddystone terurai), pohon GATT dengan nilai, notifikasi; `write` perlu `--allow-write` |
| `iotcom usb list` / `hid` / `control` / `write` / `relay` | perangkat USB dan HID (atau `--sim`): enumerasi, control IN, tulis bulk + baca (`--allow-write`), papan relay USB HID (menyalakan perlu `--allow-write`) |
| `iotcom canopen scan` / `read` / `write` / `nmt` / `monitor` | CANopen di URI `--can` apa pun atau `sim` (dua modul I/O): pemindaian node dengan identitas, baca/tulis SDO (`--allow-write`), NMT (`--allow-write`), heartbeat/PDO/emergency |
| `iotcom j1939 monitor` / `request` / `claims` | J1939 di URI `--can` apa pun atau `sim` (ECU mesin): monitor listen-only dengan SPN dan DM1 yang sudah diurai (filter `--pgn`), request seperti `vin`, `ci`, `hours`, `dm1` (jawaban multi-paket lewat BAM atau RTS/CTS), tabel alamat/NAME |
| `iotcom iec104 gi` / `read` / `monitor` / `command` / `serve` | IEC 60870-5-104 ke `-h host` atau `--sim` (RTU bay penyulang): interogasi umum, per grup, dan counter, baca, perubahan spontan, perintah dan set point dengan `--sbo` (`--allow-write`), serta simulator RTU di TCP 2404 |
| `iotcom opcua browse` / `read` / `watch` | client OPC UA (`-e opc.tcp://…`, endpoint paling aman secara default, `--no-security`, `--accept-untrusted`, `--user`) atau `--sim` untuk simulator pabrik di dalam proses; sesi hanya-baca |
| `iotcom opcua write` / `call` / `simulate` | menulis variabel atau memanggil method (perlu `--allow-write`; `write` meminta konfirmasi); menjalankan simulator pabrik di TCP |
| `iotcom sparkplug watch` / `simulate` / `write` | tampilan host Sparkplug B atas sebuah namespace (`--sim` menanam broker dan lini pembotolan), simulator edge node, penulisan NCMD/DCMD (perlu `--allow-write` dan konfirmasi) |
| `iotcom mdns browse` / `advertise` | penemuan DNS-SD (semua tipe, satu tipe, `--watch`, `--sim`) dan mengiklankan layanan dengan properti TXT |
| `iotcom payload <format> <hex>` | mengurai payload `protobuf`, `msgpack`, `ber-tlv`, `tlv`, `sparkplug`, atau `dns` sebagai frame lane dan pohon |

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
