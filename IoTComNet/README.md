<p align="center"><img src="assets/icon/icon-256.png" width="112" alt="IoTCom.Net"></p>

<h1 align="center">IoTCom.Net</h1>

<p align="center"><b>A complete IoT communication protocol library for .NET 10 — with a Rust core where it matters.</b><br>
Built by <b>Gravicode Studios</b>, led by <b>Kang Fadhil</b> · <a href="README.id.md">Bahasa Indonesia</a></p>

<p align="center">
<img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
<img alt="Rust" src="https://img.shields.io/badge/Rust-stable-B7410E">
<img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-2E9E5B">
<img alt="Status" src="https://img.shields.io/badge/status-preview-F2A900">
</p>

![IoTCom.Net Gallery — smart factory PLC](docs/images/gallery-modbus.png)

One consistent model — **client, server, publisher, subscriber** — for industrial, automotive, navigation, lighting, messaging
and healthcare protocols. Every protocol ships a **simulator**, so everything (tests, samples, notebooks, the Gallery) runs without
hardware. What .NET already does well is not rebuilt; mature libraries are wrapped; only real gaps are implemented —
in C#, or in memory-safe Rust behind a narrow C ABI.

```csharp
await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithUnitId(1));
ushort[] registers = await plc.ReadHoldingRegistersAsync(address: 0, count: 10);
```

## What's inside

| | |
|---|---|
| **Modbus TCP / RTU / ASCII** | master + slave + virtual PLC simulator, TCP pipelining, read-only safety mode, device identification — and an optional **Rust engine** (`NativeModbusClient`) |
| **OPC UA** | adapter over the OPC Foundation stack: browse, read, write behind a read-only switch, method calls, subscriptions as `IAsyncEnumerable`, secure endpoints; plant simulator server |
| **MAVLink v1 / v2** | the full common dialect generated from the official XML (Roslyn source generator — bring your own dialect), signing, UDP/TCP/serial links, ground-station helper, quadcopter simulator; frame codec mirrored in Rust |
| **NMEA 0183 · AIS** | checksum-validated parser and builder, AIS decoding (types 1–5, 18, 19, 21, 24, 27) with a vessel tracker, typed GGA/RMC/GSA/GSV/VTG/GLL/ZDA, GNSS fix aggregator, NMEA server, GPS simulator |
| **Art-Net 4 · sACN (E1.31)** | DMX512 over IP: send, receive, ArtPoll discovery, multicast, priorities, universe model with fades |
| **CoAP (RFC 7252)** | client + server over UDP: retransmission and deduplication, Observe, Block-wise, link-format discovery, SenML, greenhouse simulator; codec mirrored by a fuzzed Rust crate |
| **LoRaWAN 1.0.x** | light network server for Semtech UDP gateways (OTAA/ABP, dedup across gateways, Class A downlinks, MAC commands), packet forwarder, end-device MAC, EU868/US915/AS923-2, airtime, gateway + sensor simulator; codec mirrored by a fuzzed Rust crate |
| **DLMS/COSEM · M-Bus** | smart-meter reading over HDLC or TCP: OBIS registers, load profiles with selective access, LLS/HLS with AES-GCM ciphering, relay control behind read-only defaults, meter simulator; wired M-Bus master with scan, secondary addressing and record decoding, heat/water/electricity simulator; codecs mirrored by fuzzed Rust crates |
| **AT commands · ASTM** | cellular/GNSS modems (URC parsing, SMS, read-only mode, module simulator); lab analyzers over ASTM E1394/E1381 with NAK retransmission and an HL7 ORU bridge |
| **MQTT 3.1.1 / 5.0 · Sparkplug B** | adapter over MQTTnet: `IAsyncEnumerable` subscriptions, reconnect + resubscribe, JSON/SenML helpers, embedded broker; Sparkplug B edge node and host application (births, aliases, NDEATH will with bdSeq, rebirth, guarded writes) |
| **mDNS / DNS-SD** | responder and browser (RFC 6762/6763): announcements, known-answer suppression, goodbyes, TTL cache, type enumeration, plant simulator |
| **CAN / CAN FD** | one `ICanBus` for Linux SocketCAN, slcan USB adapters (CANable, CANtact), candleLight/gs_usb and PEAK PCAN-USB adapters, and a virtual bus; candump notation, filtered readers |
| **CANopen (CiA 301)** | master and device on any CAN bus: NMT, heartbeats, SDO (expedited, segmented, aborts), PDO mapping with event timers and SYNC, emergencies, scan, I/O module simulator; codec mirrored by a fuzzed Rust crate |
| **SAE J1939** | trucks, buses and machines on any CAN bus: address claim with NAME arbitration, BAM and RTS/CTS transport protocol, requests, decoded SPNs (EEC1, CCVS1, ET1…), DM1/DM2 trouble codes, listen-only and read-only modes, engine ECU simulator; codec mirrored by a fuzzed Rust crate |
| **IEC 60870-5-104** | SCADA master and RTU over TCP: general/group/counter interrogation, reads, spontaneous time-tagged data, commands and set points with select-before-operate, clock sync, k/w windows and t1–t3 timers, read-only by default, 20 kV feeder bay simulator with interlocks; codec mirrored by a fuzzed Rust crate |
| **NTP / SNTP** | SNTP client with the RFC 4330 checks, kiss-o'-death handling and multi-server median, NTP server with rate limiting, era-aware timestamps across 2036, drifting clock for simulations; never touches the system clock; codec mirrored by a fuzzed Rust crate |
| **NFC / NDEF** | NDEF codec (Text, URI, Smart Poster, MIME, external and Android app records, Wi-Fi credentials, chunked records), NTAG213/215/216 memory with page map, PC/SC readers on Windows, Linux and macOS, guarded tear-safe writes, virtual reader with simulated tags |
| **OMA LwM2M** | client and server over IoTCom.Net's CoAP: registration, updates and expiry, Read, Discover, Write, Execute, Observe with pmin/pmax, TLV, plain text and SenML JSON/CBOR, read-only server by default, smart street light simulator |
| **Zenoh** | adapter over the Rust `zenoh` crate (native `iotcom_zenoh`, TCP/UDP): put, delete, wildcard subscriptions as events or `IAsyncEnumerable`, queryables and `get`, read-only switch, pure C# key-expression matching, in-process virtual network; CLI `iotcom zenoh` |
| **NATS · AMQP 1.0 · Kafka** | adapters over NATS.Client.Core, AMQPNetLite and Confluent.Kafka: the shared publisher/subscriber abstractions, NATS queue groups and request/reply, AMQP confirmed or pre-settled sends with redelivery and an in-process mini broker, Kafka keys, headers, consumer groups and regex topics; not in the meta-package |
| **Bluetooth LE** | central over a Rust library built on btleplug (WinRT, BlueZ, CoreBluetooth): scan, GATT read/write/notify with a read-only switch; advertising data, iBeacon, Eddystone and GATT value codecs; virtual radio |
| **USB · HID** | raw control/bulk/interrupt transfers (Rust nusb) and HID reports (hidapi) with read-only switches, `UseUsbBulk` byte-stream transport, USB HID relay boards, virtual bus |
| **ISO-TP · UDS · OBD-II** | ISO 15765-2 as a fuzzed **Rust** state machine; UDS tester (sessions, security access, DIDs, DTCs, routines, read-only mode), OBD-II scan tool and an ECU simulator |
| **HL7 v2 · MLLP** | ER7 parser/builder with escaping, MLLP sender/receiver with ACK matching, LOINC vital signs, bedside-monitor simulator (sepsis, hypoxia, …) |
| **DICOM** | adapter over fo-dicom: Storage SCP/SCU (C-STORE, C-ECHO), windowed renderer to PNG, synthetic CT/MR/X-ray studies with planted findings |
| **Payload codecs** | SenML (RFC 8428, JSON + CBOR), Protobuf and MessagePack adapters with schema-less inspection, simple TLV and BER-TLV (EMV); one `IPayloadCodec<T>` contract |
| **Framing** | CRC catalogue (23 presets, 8–64 bit), LRC, SLIP, COBS, HDLC, streaming decoders on `System.IO.Pipelines` |
| **Core** | TCP / serial / in-memory transports, traffic tap (*frame lane*), OpenTelemetry metrics & traces, reconnect policy, hosting + health checks |

Plus: the **IoTCom.Net Gallery** desktop app, the **`iotcom` CLI**, the **VS Code extension** (frame viewer, traffic monitor), an **edge gateway** web sample with a live HMI
dashboard, console samples, `dotnet new` **templates**, Polyglot **notebooks** and **documentation in English and
Bahasa Indonesia**.

## See it

<table>
<tr><td><img src="docs/images/gateway-dashboard.png" alt="Gateway dashboard"><br><sub>Edge gateway: Modbus → MQTT with a live HMI dashboard and the decoded wire</sub></td>
<td><img src="docs/images/gallery-traffic.png" alt="Gallery traffic"><br><sub>Gallery: every frame decoded field by field</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-can-uds.png" alt="Vehicle diagnostics"><br><sub>Vehicle diagnostics: OBD-II and UDS over CAN, ISO-TP in Rust, against a simulated engine ECU</sub></td></tr>
<tr><td><img src="docs/images/gallery-hl7-icu.png" alt="ICU"><br><sub>ICU bedside monitors over HL7/MLLP: NEWS2, trends and an LLM SBAR note</sub></td>
<td><img src="docs/images/gallery-dicom-ai.png" alt="DICOM AI"><br><sub>DICOM C-STORE → windowed viewer → vision-model pre-read</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-mavlink.png" alt="MAVLink drone"><br><sub>Drone telemetry over MAVLink: artificial horizon, flight track and acknowledged commands</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-coap.png" alt="CoAP greenhouse"><br><sub>CoAP greenhouse: Observe, Block-wise and a live message sequence chart on a lossy link</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-metering.png" alt="Smart meter reading"><br><sub>Smart meter reading: DLMS/COSEM on an LCD faceplate, two days of load profile, and M-Bus sub-meters</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-lorawan.png" alt="LoRaWAN network monitor"><br><sub>LoRaWAN network monitor: gateways, spreading-factor reach and every uplink on a radio map, through a light network server</sub></td></tr>
<tr><td><img src="docs/images/gallery-nmea.png" alt="NMEA"><br><sub>GNSS tracker over NMEA 0183</sub></td>
<td><img src="docs/images/gallery-lighting.png" alt="Art-Net"><br><sub>Stage lighting over Art-Net</sub></td></tr>
<tr><td><img src="docs/images/gallery-workbench.png" alt="Workbench"><br><sub>Frame & checksum workbench</sub></td>
<td><img src="docs/images/cli.png" alt="CLI"><br><sub><code>iotcom</code> — decode, read, CRC from the terminal</sub></td></tr>
</table>

## Get started

```bash
dotnet add package IoTCom.Net --prerelease            # libraries (meta-package)
dotnet tool install -g IoTCom.Net.Cli --prerelease    # iotcom CLI
dotnet new install IoTCom.Net.Templates               # dotnet new iotcom-console / iotcom-worker

iotcom modbus serve --port 1502 --simulate            # a virtual PLC…
iotcom modbus read --port 1502 --table input --count 8 --watch 1000   # …and a live view
```

Read the [quickstart](docs/en/getting-started/quickstart.md), then browse the [documentation](docs/en/index.md).

## Run the apps from source

```bash
dotnet run --project gallery/IoTCom.Net.Gallery                          # desktop Gallery
dotnet run --project samples/web/IoTCom.Gateway --urls http://localhost:5080
dotnet run --project samples/console/ModbusMaster -- --simulate
```

## Build and test

```bash
dotnet build IoTCom.Net.slnx
dotnet test tests/IoTCom.Net.Tests                  # 300+ tests incl. cross-language conformance
cd rust && cargo test --workspace && cargo build --release   # Rust core + iotcom_modbus native library
python build/check_docs_parity.py                   # EN/ID parity + link check
```

## Repository layout

```
src/            C# packages (Abstractions, Core, Framing, Transport.Serial, Transport.Can, Protocols.*, Protocols.Hl7, Adapters.Mqtt, Adapters.Dicom, Serialization.SenML, Hosting, Native.Modbus, meta)
rust/           Rust workspace: iotcom-core (sans-I/O Machine), iotcom-ffi-support, iotcom-modbus, native cdylibs
conformance/    shared test vectors run by both C# and Rust
tests/          xUnit tests
samples/        console samples and the IoTCom.Gateway web sample
gallery/        IoTCom.Net Gallery (Avalonia) + headless screenshot renderer
tools/          iotcom CLI
templates/      dotnet new templates
notebooks/      Polyglot notebooks (EN + ID)
docs/           documentation (docs/en, docs/id) and images
build/          native cross-build, docs/notebook gates, screenshot tooling
```

## Project

- [PLAN.md](PLAN.md) — roadmap · [Progress.md](Progress.md) — development tracking
- [solution-design.md](solution-design.md) — the original design document (Bahasa Indonesia)
- [CONTRIBUTING.md](CONTRIBUTING.md) · [SECURITY.md](SECURITY.md) · [CHANGELOG.md](CHANGELOG.md)
- License: [Apache-2.0](LICENSE)

---

<p align="center"><sub>IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.</sub></p>
