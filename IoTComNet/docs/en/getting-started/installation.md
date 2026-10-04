---
title: Installation
translation-status: synced
---

# Installation

IoTCom.Net targets **.NET 10**. Install the meta-package for the common set, or pick individual packages to keep
deployments small (every package is trimmable and NativeAOT-compatible).

```bash
dotnet add package IoTCom.Net --prerelease
```

## Packages

| Package | What you get |
|---|---|
| `IoTCom.Net` | Meta-package: everything below except the native engine, plus hosting extensions (`AddModbusClient`, `AddMqtt`, …) |
| `IoTCom.Net.Abstractions` | Contracts: `IEndpoint`, `IClientEndpoint`, `IServerEndpoint`, `IPublisher<T>`, `ISubscriber<T>`, `ITransport`, `ITrafficTap`, exceptions |
| `IoTCom.Net.Core` | TCP and in-memory transports over `System.IO.Pipelines`, endpoint base, traffic tap, diagnostics, reconnect policy, native loader |
| `IoTCom.Net.Framing` | CRC catalogue (23 presets), LRC, SLIP, COBS, HDLC, line framing |
| `IoTCom.Net.Transport.Serial` | `UseSerial(...)` for every endpoint (System.IO.Ports) |
| `IoTCom.Net.Protocols.Modbus` | Modbus TCP/RTU/ASCII master + slave + simulator |
| `IoTCom.Net.Native.Modbus` | Rust protocol engine for Modbus (`NativeModbusClient`) |
| `IoTCom.Net.Protocols.Nmea` | NMEA 0183 reader, server, simulator |
| `IoTCom.Net.Protocols.Dmx` | Art-Net 4 and sACN (E1.31) |
| `IoTCom.Net.Adapters.Mqtt` | MQTT adapter over MQTTnet + embedded broker |
| `IoTCom.Net.Serialization.SenML` | SenML JSON/CBOR |
| `IoTCom.Net.Hosting` | `AddIoTCom()`, hosted lifecycle, health checks |
| `IoTCom.Net.Templates` | `dotnet new` templates |
| `IoTCom.Net.Cli` | `iotcom` command-line tool |

## Tools

```bash
dotnet tool install -g IoTCom.Net.Cli --prerelease      # the iotcom command
dotnet new install IoTCom.Net.Templates                  # project templates
```

## Build from source

```bash
git clone <repository> && cd IoTComNet
dotnet build IoTCom.Net.slnx
dotnet test tests/IoTCom.Net.Tests
cd rust && cargo build --release   # optional: the Rust engine (iotcom_modbus)
```

Next: [Quickstart](quickstart.md).
