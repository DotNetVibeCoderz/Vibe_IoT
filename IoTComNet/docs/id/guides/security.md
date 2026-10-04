---
title: Keamanan dan keselamatan
translation-status: synced
---

# Keamanan dan keselamatan

IoTCom.Net berbicara dengan peralatan yang bergerak, memanas, menyakelar, dan menyala. Perlakukan penulisan sebagai
tindakan fisik.

## Aman secara default

- **Mode read-only.** `ModbusClient.AsReadOnly()` dan `ModbusServer.AsReadOnly()` memblokir setiap penulisan.
  Penulisan pada client read-only melempar `ReadOnlyModeException` sebelum apa pun dikirim.
- **CLI bertanya dua kali.** `iotcom modbus write` menolak berjalan tanpa `--allow-write` lalu meminta konfirmasi
  (lewati hanya di skrip, dengan `--yes`).
- **Gateway dapat dikunci.** `Gateway:AllowWrites=false` menonaktifkan kontrol dashboard dan endpoint penulisan.

## Input tak tepercaya

Setiap parser memperlakukan input dari jaringan dan perangkat sebagai tak tepercaya: panjang dibatasi (PDU Modbus
≤ 253 byte, frame dibatasi per codec), buffer penerimaan dibatasi, frame rusak dibuang dan stream diresinkronisasi.
Codec Rust juga diuji dengan uji decoder acak, dan target fuzz ada di roadmap.

## Keamanan transport

| Protokol | Rekomendasi |
|---|---|
| Modbus | tidak ada keamanan di protokolnya: isolasi jaringan (VLAN/firewall), jangan buka 502 ke internet, pasang gateway di depannya |
| MQTT | TLS (`WithTls()`, port 8883), kredensial, ACL broker per topik |
| Art-Net / sACN | jaringan lampu terpisah; unicast bila memungkinkan |
| NMEA | perlakukan posisi sebagai tak tepercaya di sistem terkait keselamatan |

Kredensial tidak pernah dicatat. Traffic tap menangkap payload — lindungi hasil tangkapan seperti data di dalamnya.

## Rantai pasok

- `cargo deny` / `cargo audit` dan `dotnet list package --vulnerable` berjalan di CI.
- Dependensi dipin secara terpusat (`Directory.Packages.props`, workspace `Cargo.toml`).
- Tidak ada dependensi GPL/AGPL di paket inti; lisensi ditinjau sebelum menambah crate atau paket.

Laporkan kerentanan seperti dijelaskan di [SECURITY.md](../../../SECURITY.md).
