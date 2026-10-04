---
title: Dokumentasi IoTCom.Net
translation-status: synced
---

# Dokumentasi IoTCom.Net

**IoTCom.Net** adalah library komunikasi untuk sistem IoT dan embedded di .NET 10, dengan inti Rust untuk bagian
low-level. Satu model yang konsisten — *client, server, publisher, subscriber* — mencakup protokol industri, navigasi,
pencahayaan, dan pesan, dan setiap protokol dilengkapi simulator sehingga Anda bisa membangun dan menguji tanpa
perangkat keras.

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil. · [English](../en/index.md)

![Galeri IoTCom.Net](../images/gallery-modbus.png)

## Mulai

| | |
|---|---|
| [Instalasi](getting-started/installation.md) | Paket, platform yang didukung, izin |
| [Mulai cepat](getting-started/quickstart.md) | Membaca PLC (simulasi) dalam 15 baris |
| [Platform & izin](getting-started/platforms.md) | Windows, Linux, macOS, Raspberry Pi, container |

## Pahami

| | |
|---|---|
| [Arsitektur](concepts/architecture.md) | Tier kurasi, lapisan, sans-I/O, peran Rust |
| [Endpoint & transport](concepts/endpoints-and-transports.md) | Peran, siklus hidup, builder, reconnect, model error |
| [Traffic tap & observability](concepts/traffic-tap.md) | Penangkapan frame, frame lane, OpenTelemetry |

## Protokol

| Protokol | Peran | Paket |
|---|---|---|
| [Modbus TCP / RTU / ASCII](protocols/modbus.md) | master · slave · simulator | `IoTCom.Net.Protocols.Modbus` (+ `Native.Modbus`) |
| [NMEA 0183](protocols/nmea.md) | pembaca · server · simulator | `IoTCom.Net.Protocols.Nmea` |
| [Art-Net 4 · sACN (DMX512)](protocols/dmx.md) | kirim · terima · discovery | `IoTCom.Net.Protocols.Dmx` |
| [MQTT 3.1.1 / 5.0](protocols/mqtt.md) | publish · subscribe · broker | `IoTCom.Net.Adapters.Mqtt` |
| [Framing & CRC](protocols/framing.md) | codec | `IoTCom.Net.Framing` |
| [SenML](protocols/senml.md) | codec | `IoTCom.Net.Serialization.SenML` |
| Serial RS-232/485 | transport | `IoTCom.Net.Transport.Serial` |

[Roadmap](../../PLAN.md) berisi protokol berikutnya (CAN/UDS, MAVLink, DLMS, CoAP, adapter OPC UA, BLE, …).

## Membangun aplikasi

| | |
|---|---|
| [Hosting & dependency injection](guides/hosting.md) | `AddIoTCom()`, endpoint bernama, health check |
| [Sampel edge gateway](guides/gateway.md) | Jembatan Modbus → MQTT dengan dashboard HMI langsung |
| [Simulator](guides/simulators.md) | Mengembangkan dan menguji tanpa perangkat keras |
| [Deployment](guides/deployment.md) | systemd, Windows Service, Docker, NativeAOT |
| [Keamanan & keselamatan](guides/security.md) | Mode read-only, TLS, input tak tepercaya |

## Perkakas

| | |
|---|---|
| [Galeri](tools/gallery.md) | Aplikasi desktop: jalankan setiap protokol, baca kodenya, periksa byte-nya |
| [CLI (`iotcom`)](tools/cli.md) | Baca, tulis, layani, urai dari terminal |
| [Template](tools/templates.md) | `dotnet new iotcom-console`, `iotcom-worker` |
| [Notebook](tools/notebooks.md) | Polyglot notebook per protokol, EN dan ID |

## Di balik layar

- [Lapisan native: Rust & C ABI](native/rust-ffi.md)
- [Berkontribusi protokol](contributing/adding-a-protocol.md)
- [Glosarium](glossary.md)
