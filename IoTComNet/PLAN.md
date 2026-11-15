# IoTCom.Net — Roadmap

Built by Gravicode Studios, led by Kang Fadhil. The design behind this plan is in [solution-design.md](solution-design.md);
day-to-day status is tracked in [Progress.md](Progress.md).

Priorities follow the design: **P0** foundation · **P1** first release · **P2** expansion · **P3** optional / community.
Each protocol is "done" only when it meets the [Definition of Done](#definition-of-done-per-protocol).

**Where we are (2026-11-15):** Phase 0 and Phase 1 are complete. Phase 2 is well past half (11 of 15 protocol items);
the latest release is `0.26.0-preview.1`. What is left is listed under [Remaining work](#remaining-work).

## Phase 0 — Foundation → `0.1.0-preview` ✅ complete

| Item | Status |
|---|---|
| Repository, solution, Central Package Management, analyzers, trimming/AOT flags | ✅ |
| Rust workspace: `iotcom-core` (sans-I/O `Machine`), `iotcom-ffi-support` (`ffi_guard`, status codes, last error, ABI version) | ✅ |
| Native loader (RID probing, `IOTCOM_NATIVE_PATH`), `LibraryImport` + `SafeHandle` bindings | ✅ |
| Abstractions + Core: endpoints, `ITransport` over Pipelines, TCP / in-memory transports, traffic tap, diagnostics, reconnect | ✅ |
| Framing: CRC catalogue (23 presets), LRC, SLIP, COBS, HDLC, line framing + shared conformance vectors | ✅ |
| Modbus TCP/RTU/ASCII master + slave + simulator (C#) and Rust master engine (`IoTCom.Net.Native.Modbus`) | ✅ |
| MQTT adapter (MQTTnet) + embedded broker | ✅ |
| Serial transport | ✅ |
| Hosting: `AddIoTCom()`, hosted lifecycle, health checks | ✅ |
| CLI `iotcom` (info, ports, crc, frame, modbus, nmea, artnet, mqtt) | ✅ |
| Docs EN/ID first edition (25 pages each) + parity/link gate | ✅ |
| Templates `iotcom-console`, `iotcom-worker` | ✅ |
| CI matrix (Windows, Ubuntu, macOS, Alpine) + native cross-build workflow | ✅ green since `0.1.0-preview.1` |

Pulled forward from Phase 1 because they make the foundation demonstrable: **NMEA 0183**, **Art-Net / sACN**, **SenML**,
the **Gallery** (5 demos), the **IoTCom.Gateway** web sample and **notebooks** (6 EN/ID pairs).

## Phase 1 — First release → `0.5.0-beta` ✅ complete (`0.2.0` – `0.15.0-preview.1`)

Open follow-ups per protocol are collected under [Remaining work](#remaining-work) → *Follow-ups*.

| Item | Priority | Delivered in |
|---|---|---|
| CAN / CAN FD transport (`ICanBus`): SocketCAN, slcan, virtual bus · gs_usb/candleLight, PCAN-USB | P0 | `0.3.0` · `0.15.0` |
| ISO-TP (Rust), UDS, OBD-II (tester + ECU simulator) | P1 | `0.3.0` |
| CoAP (Observe, Block-wise) + Rust codec twin | P1 | `0.4.0` |
| MAVLink v1/v2 + dialect source generator + Rust frame twin | P1 | `0.5.0` |
| LoRaWAN MAC (device simulator, light network server) + Semtech UDP forwarder | P1 | `0.8.0` |
| DLMS/COSEM (HDLC + APDU) and wired M-Bus | P1 | `0.9.0` |
| NMEA: AIS decoding (types 1–5, 18, 19, 21, 24, 27) | P1 | `0.10.0` |
| mDNS / DNS-SD | P1 | `0.11.0` |
| AT command engine (cellular modules, URC parser) | P1 | `0.10.0` |
| HL7 v2 MLLP · ASTM E1394/E1381 + HL7 bridge | P1 | `0.2.0` · `0.10.0` |
| DICOM adapter (fo-dicom): Storage SCP/SCU, renderer, synthetic studies (added on request) | P1 | `0.2.0` |
| Sparkplug B · OPC UA adapter · BLE central · USB + HID transport | P1 | `0.11.0` · `0.12.0` · `0.13.0` · `0.14.0` |
| Protobuf / MessagePack adapters, TLV helpers (`IPayloadCodec<T>`) | P1 | `0.11.0` |
| Gallery ≥ 10 demos, Blazor live dashboard, templates complete | P1 | 10 demos by `0.5.0` (23 today) |
| VS Code extension v0.1 (frame viewer, traffic monitor, `iotcom rpc`) | P1 | `0.7.0` |
| `cargo-fuzz` targets for every Rust `handle_input`, scheduled in CI | P0 | 6 targets in Phase 1 (10 today) |
| Generated C# bindings (csbindgen) checked for drift in CI; committed cbindgen headers | P1 | after `0.15.0` |
| `iotcom sniff` (TCP/UDP relays, CAN capture) + pcapng export | P1 | `0.6.0` |

## Phase 2 — Expansion → `1.0.0` 🚧 in progress

| Item | Status |
|---|---|
| CANopen | ✅ `0.16.0-preview.1` |
| SAE J1939 | ✅ `0.17.0-preview.1` |
| IEC 60870-5-104 | ✅ `0.18.0-preview.1` |
| NTP / SNTP | ✅ `0.19.0-preview.1` |
| NFC / NDEF | ✅ `0.20.0-preview.1` |
| OMA LwM2M | ✅ `0.21.0-preview.1` |
| Zenoh + Kafka / NATS / AMQP adapters | ✅ `0.22.0-preview.1` (Zenoh native library not shipped yet, see Progress) |
| EtherNet/IP (explicit messaging) | ✅ `0.26.0-preview.1` |
| EtherCAT master | ⏳ |
| SWD/JTAG + DFU (probe-rs) | ⏳ |
| Matter controller + device/bridge | ⏳ (scope decided 2026-11-15: open question 2) |
| KNXnet/IP | ✅ `0.24.0-preview.1` |
| BACnet/IP: adapter + own server API | ✅ `0.25.0-preview.1` |
| DTLS 1.2/1.3 (also unlocks CoAP/LwM2M security) | ⏳ |
| OCPP 1.6J / 2.0.1 | ✅ `0.23.0-preview.1` |
| Complete EN/ID documentation | 🚧 56 + 56 pages; grows with every item above |
| Gallery with all 17 design use cases | 🚧 29 demos, 16 of 17 use cases covered (#12 firmware probing waits for SWD/DFU) |
| Signed and notarised releases | ✅ wired 2026-11-15 (Authenticode, NuGet author signing, Developer ID + notarisation; active once the signing secrets are added) |

## Phase 3 — Optional

PROFINET (DCP), DNP3 / IEC 61850 (only if licensing allows), SOME/IP, XCP/CCP, SUIT/MCUboot, OSCORE/EDHOC, PTP,
ISO 15118, WASM build of the sans-I/O machines, Android/iOS subsets.

## Remaining work

### To reach `1.0.0` (Phase 2)

1. **Protocols:** EtherCAT master, SWD/JTAG + DFU, Matter (controller and device/bridge), DTLS.
2. **Gallery use cases still missing** (design §8 catalogue):
   - #12 probe & flash firmware — SWD/JTAG, DFU, RTT;
3. **Documentation:** a page per new protocol, a complete support matrix, EN/ID parity kept.
4. **Releases:** add the signing secrets (see *Releasing and signing*); a `1.0.0` API review (public surface, naming, obsolete members).

### Follow-ups on delivered protocols

| Area | Open items |
|---|---|
| CAN adapters | Kvaser, Vector; hardware-in-the-loop run of gs_usb and PCAN |
| Automotive | UDS flashing helpers, DoIP; J1939 ETP, DM3/DM11, NMEA 2000 fast packet; CANopen SDO block, LSS, EDS import |
| CoAP / LwM2M | DTLS, OSCORE, CoAP over TCP; LwM2M bootstrap, Create/Delete, Send, block-wise |
| MAVLink | mission protocol, FTP, routing |
| LoRaWAN | Class B/C, ADR decisions, Basics Station |
| Metering | DLMS suites 1/2, push; wireless M-Bus / OMS |
| Telecontrol | IEC 104 file transfer, IEC 62351, IEC 101 serial |
| Time | NTS, symmetric/broadcast modes, clock filter |
| NFC | MIFARE Classic, Type 4/5 tags, NTAG password, test with a real tag |
| Discovery / plant | mDNS probing/conflicts, IPv6; Sparkplug data sets, templates, offline buffering |
| Field devices | AT PDU SMS, CMUX; ASTM host query; DMX RDM |
| Connectivity | BLE peripheral role, pairing, L2CAP; USB isochronous, hotplug, gadget role |
| EtherNet/IP | Forward_Open connected messaging, Logix UDTs/STRING, Read Tag Fragmented, tag browsing, CIP Security (implicit I/O is Phase 3) |
| Industrial adapters | OPC UA events/A&C, history, complex types |
| Building automation | BACnet segmentation, BBMD/foreign devices, alarms/events, schedules, trend logs, BACnet/SC, MS/TP; KNX Secure, KNXnet/IP over TCP, device management, busmonitor mode |
| EV charging | OCPP firmware management, local authorization lists, reservations, 2.0.1 device-model reports, offline transaction queue, OCPP 2.1 |
| Messaging | Zenoh TLS/QUIC, storages, liveliness, **native library (blocked on zenoh moving to a fixed `lz4_flex`)**; NATS JetStream; AMQP transactions, dynamic nodes; Kafka admin, transactions |

### Infrastructure and verification

- Hardware-in-the-loop rig (serial/RS-485, CAN adapters, NFC tags, Art-Net broadcast) — open question 8.
- Native binaries for Linux, macOS and Windows ARM64 come only from CI; nothing is built locally for those RIDs.

## Open questions (from the design)

| # | Question | Status |
|---|---|---|
| 1 | First CAN adapters (PCAN, Kvaser, slcan, gs_usb/candleLight, Vector)? | Answered: slcan (0.3), gs_usb + PCAN (0.15); Kvaser/Vector open |
| 2 | Matter: controller only, or device/bridge too? | Answered 2026-11-15: both — controller and device/bridge |
| 3 | BACnet and OPC UA: adapters only, or our own server API? | Answered: OPC UA adapter + simulator server (0.12); BACnet 2026-11-15: adapter **and** our own server API |
| 4 | Final licence policy for industrial modules (Apache-2.0 today) | Open |
| 5 | Android/iOS as official targets? | Open (Phase 3) |
| 6 | Managed fallbacks for platforms without native support | Answered: Modbus has a managed engine; later protocols are managed C# with Rust codec twins |
| 7 | Gallery distribution: installer, portable, Store/Flatpak/Homebrew? | Answered 2026-11-15: signed portable archives (win-x64, linux-x64, osx-arm64) on each GitHub release; installers and stores later |
| 8 | Reference hardware for hardware-in-the-loop tests | Open |

## Definition of Done (per protocol)

Public API documented (XML docs + EN/ID page) · unit + conformance + fuzz (if Rust) tests · simulator · console sample ·
notebook pair · Gallery demo (when relevant) · basic benchmark · entry in the support matrix.
