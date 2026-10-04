---
title: Instalasi
translation-status: synced
---

# Instalasi

IoTCom.Net menargetkan **.NET 10**. Pasang meta-paket untuk set umum, atau pilih paket satu per satu agar deployment
tetap kecil (setiap paket mendukung trimming dan NativeAOT).

```bash
dotnet add package IoTCom.Net --prerelease
```

## Paket

| Paket | Isi |
|---|---|
| `IoTCom.Net` | Meta-paket: semua di bawah kecuali mesin native, plus ekstensi hosting (`AddModbusClient`, `AddMqtt`, …) |
| `IoTCom.Net.Abstractions` | Kontrak: `IEndpoint`, `IClientEndpoint`, `IServerEndpoint`, `IPublisher<T>`, `ISubscriber<T>`, `ITransport`, `ITrafficTap`, exception |
| `IoTCom.Net.Core` | Transport TCP dan in-memory di atas `System.IO.Pipelines`, basis endpoint, traffic tap, diagnostik, kebijakan reconnect, loader native |
| `IoTCom.Net.Framing` | Katalog CRC (23 preset), LRC, SLIP, COBS, HDLC, framing baris |
| `IoTCom.Net.Transport.Serial` | `UseSerial(...)` untuk setiap endpoint (System.IO.Ports) |
| `IoTCom.Net.Protocols.Modbus` | Master + slave + simulator Modbus TCP/RTU/ASCII |
| `IoTCom.Net.Native.Modbus` | Mesin protokol Rust untuk Modbus (`NativeModbusClient`) |
| `IoTCom.Net.Protocols.Nmea` | Pembaca, server, simulator NMEA 0183 |
| `IoTCom.Net.Protocols.Dmx` | Art-Net 4 dan sACN (E1.31) |
| `IoTCom.Net.Adapters.Mqtt` | Adapter MQTT di atas MQTTnet + broker tertanam |
| `IoTCom.Net.Serialization.SenML` | SenML JSON/CBOR |
| `IoTCom.Net.Hosting` | `AddIoTCom()`, siklus hidup ter-host, health check |
| `IoTCom.Net.Templates` | Template `dotnet new` |
| `IoTCom.Net.Cli` | Perkakas baris perintah `iotcom` |

## Perkakas

```bash
dotnet tool install -g IoTCom.Net.Cli --prerelease      # perintah iotcom
dotnet new install IoTCom.Net.Templates                  # template proyek
```

## Build dari source

```bash
git clone <repository> && cd IoTComNet
dotnet build IoTCom.Net.slnx
dotnet test tests/IoTCom.Net.Tests
cd rust && cargo build --release   # opsional: mesin Rust (iotcom_modbus)
```

Berikutnya: [Mulai cepat](quickstart.md).
