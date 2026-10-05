---
title: IoTCom.Net documentation
translation-status: synced
---

# IoTCom.Net documentation

**IoTCom.Net** is a communication library for IoT and embedded systems on .NET 10, with a Rust core for the low-level
parts. One consistent model — *client, server, publisher, subscriber* — covers industrial, automotive, navigation, lighting and
messaging and healthcare protocols, and every protocol ships a simulator so you can build and test without hardware.

> Built by Gravicode Studios, led by Kang Fadhil. · [Bahasa Indonesia](../id/index.md)

![IoTCom.Net Gallery](../images/gallery-modbus.png)

## Start

| | |
|---|---|
| [Installation](getting-started/installation.md) | Packages, supported platforms, permissions |
| [Quickstart](getting-started/quickstart.md) | Read a (simulated) PLC in 15 lines |
| [Platforms & permissions](getting-started/platforms.md) | Windows, Linux, macOS, Raspberry Pi, containers |

## Understand

| | |
|---|---|
| [Architecture](concepts/architecture.md) | Curation tiers, layers, sans-I/O, where Rust fits |
| [Endpoints & transports](concepts/endpoints-and-transports.md) | Roles, lifecycle, builders, reconnect, error model |
| [Traffic tap & observability](concepts/traffic-tap.md) | Frame capture, frame lane, OpenTelemetry |

## Protocols

| Protocol | Roles | Package |
|---|---|---|
| [Modbus TCP / RTU / ASCII](protocols/modbus.md) | master · slave · simulator | `IoTCom.Net.Protocols.Modbus` (+ `Native.Modbus`) |
| [NMEA 0183](protocols/nmea.md) | reader · server · simulator | `IoTCom.Net.Protocols.Nmea` |
| [Art-Net 4 · sACN (DMX512)](protocols/dmx.md) | send · receive · discovery | `IoTCom.Net.Protocols.Dmx` |
| [CAN / CAN FD](protocols/can.md) | send · receive · SocketCAN · slcan · virtual | `IoTCom.Net.Transport.Can` |
| [ISO-TP · UDS · OBD-II](protocols/uds.md) | tester · scan tool · ECU simulator | `IoTCom.Net.Protocols.IsoTp`, `.Uds` |
| [HL7 v2 over MLLP](protocols/hl7.md) | send · receive · ACK · bedside simulator | `IoTCom.Net.Protocols.Hl7` |
| [DICOM](protocols/dicom.md) | C-STORE SCP/SCU · rendering · synthetic studies | `IoTCom.Net.Adapters.Dicom` |
| [MQTT 3.1.1 / 5.0](protocols/mqtt.md) | publish · subscribe · broker | `IoTCom.Net.Adapters.Mqtt` |
| [Framing & CRC](protocols/framing.md) | codec | `IoTCom.Net.Framing` |
| [SenML](protocols/senml.md) | codec | `IoTCom.Net.Serialization.SenML` |
| Serial RS-232/485 | transport | `IoTCom.Net.Transport.Serial` |

The [roadmap](../../PLAN.md) lists the protocols coming next (CAN/UDS, MAVLink, DLMS, CoAP, OPC UA adapter, BLE, …).

## Build things

| | |
|---|---|
| [Hosting & dependency injection](guides/hosting.md) | `AddIoTCom()`, named endpoints, health checks |
| [Edge gateway sample](guides/gateway.md) | Modbus → MQTT bridge with a live HMI dashboard |
| [Medical devices + AI](guides/medical-ai.md) | HL7 vitals → NEWS2 dashboard → LLM note; DICOM → vision pre-read |
| [Simulators](guides/simulators.md) | Develop and test without hardware |
| [Deployment](guides/deployment.md) | systemd, Windows Service, Docker, NativeAOT |
| [Security & safety](guides/security.md) | Read-only mode, TLS, untrusted input |

## Tools

| | |
|---|---|
| [Gallery](tools/gallery.md) | Desktop app: run every protocol, read its code, inspect its bytes |
| [CLI (`iotcom`)](tools/cli.md) | Read, write, serve, decode, sniff from the terminal |
| [Templates](tools/templates.md) | `dotnet new iotcom-console`, `iotcom-worker` |
| [Notebooks](tools/notebooks.md) | Polyglot notebooks per protocol, EN and ID |

## Under the hood

- [Native layer: Rust & the C ABI](native/rust-ffi.md)
- [Contributing a protocol](contributing/adding-a-protocol.md)
- [Glossary](glossary.md)
