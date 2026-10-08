# IoTCom.Net — Roadmap

Built by Gravicode Studios, led by Kang Fadhil. The design behind this plan is in [solution-design.md](solution-design.md);
day-to-day status is tracked in [Progress.md](Progress.md).

Priorities follow the design: **P0** foundation · **P1** first release · **P2** expansion · **P3** optional / community.
Each protocol is "done" only when it meets the Definition of Done (bottom of this page).

## Phase 0 — Foundation → `0.1.0-preview` (released)

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
| Docs skeleton EN/ID → full first edition (25 pages each) + parity/link gate | ✅ |
| Templates `iotcom-console`, `iotcom-worker` | ✅ |
| CI matrix (Windows, Ubuntu, macOS, Alpine) + native cross-build workflow | ✅ defined — first run pending a hosted repository |

Pulled forward from Phase 1 because they make the foundation demonstrable: **NMEA 0183**, **Art-Net / sACN**, **SenML**,
the **Gallery** (5 demos), the **IoTCom.Gateway** web sample and **notebooks** (6 EN/ID pairs).

## Phase 1 — First release → `0.5.0-beta` (± 8–12 weeks)

| Item | Priority | Notes |
|---|---|---|
| CAN / CAN FD transport (`ICanBus`): SocketCAN + one USB adapter (gs_usb / candleLight or PCAN) | P0 | ✅ `0.3.0-preview.1`: SocketCAN + slcan adapters + virtual bus (C#; see Progress decisions) · PCAN/Kvaser/gs_usb ⏳ |
| ISO-TP, UDS, OBD-II (tester + ECU simulator) | P1 | ✅ `0.3.0-preview.1`: ISO-TP in Rust; UDS/OBD-II/ECU simulator in C# · flashing helpers, DoIP ⏳ |
| CoAP (Observe, Block-wise) | P1 | ✅ `0.4.0-preview.1`: C# client/server + Rust codec twin (conformance + fuzz) · DTLS, OSCORE, CoAP-over-TCP ⏳ |
| MAVLink v1/v2 + dialect source generator | P1 | ✅ `0.5.0-preview.1`: C# runtime + Roslyn generator (common dialect, custom dialects) + Rust frame codec twin · mission protocol, FTP, routing ⏳ |
| LoRaWAN MAC (device simulator, light network server) + Semtech UDP forwarder | P1 | ✅ `0.8.0-preview.1`: managed C# runtime (server, forwarder, device MAC, simulator) + fuzzed Rust codec twin · Class B/C, ADR decisions, Basics Station ⏳ |
| DLMS/COSEM (HDLC + APDU) and wired M-Bus | P1 | |
| NMEA: AIS decoding | P1 | |
| mDNS / DNS-SD (discovery for Gallery and CLI) | P1 | |
| AT command engine (cellular modules, URC parser) | P1 | |
| HL7 v2 MLLP + ASTM E1394 (lab analyzers) | P1 | ✅ HL7 v2 + MLLP + simulator in `0.2.0-preview.1` · ASTM E1394 ⏳ |
| DICOM adapter (fo-dicom): Storage SCP/SCU, renderer, synthetic studies | P1 | ✅ `0.2.0-preview.1` (added on request: medical use cases) |
| Sparkplug B, OPC UA adapter (OPCFoundation.NetStandard), BLE central, USB transport | P1 | |
| Protobuf / MessagePack adapters, TLV helpers | P1 | |
| Gallery ≥ 10 demos, Blazor live dashboard, templates complete | P1 | ✅ 10 demos (incl. MAVLink drone, CoAP greenhouse, vehicle diagnostics, ICU monitors, imaging AI pre-read) |
| VS Code extension v0.1 (Protocol Explorer, frame/hex viewer, traffic monitor via the CLI over JSON-RPC) | P1 | ✅ 0.7.0-preview.1 |
| `cargo-fuzz` targets for every Rust `handle_input`, scheduled in CI | P0 | ✅ 6 targets (Modbus decode/master/PDU, ISO-TP, CoAP, MAVLink); smoke run per change + nightly 10 min |
| Generated C# bindings (csbindgen) checked for drift in CI; committed cbindgen header | P1 | bindings are hand-written today |
| `iotcom sniff` (Modbus TCP proxy sniffer) and pcapng export from the traffic tap | P1 | ✅ `0.6.0-preview.1`: TCP/UDP relays + CAN capture, Wireshark-native pcapng (tshark-checked in CI), Gallery export |

## Phase 2 — Expansion → `1.0.0` (± 12 weeks)

CANopen, J1939, IEC 60870-5-104, EtherNet/IP (explicit), EtherCAT master, SWD/JTAG + DFU (probe-rs), Matter
controller, KNXnet/IP, BACnet/IP, LwM2M, Zenoh, DTLS 1.2/1.3, OCPP 1.6J/2.0.1, NTP/SNTP, NFC/NDEF, adapters for
Kafka/NATS/AMQP; complete EN/ID documentation; Gallery with all 17 use cases; signed and notarised releases.

## Phase 3 — Optional

PROFINET (DCP), DNP3 / IEC 61850 (only if licensing allows), SOME/IP, XCP/CCP, SUIT/MCUboot, OSCORE/EDHOC, PTP,
ISO 15118, WASM build of the sans-I/O machines, Android/iOS subsets.

## Open questions (from the design)

1. First CAN adapters (PCAN, Kvaser, slcan, gs_usb/candleLight, Vector)?
2. Matter: controller only, or device/bridge too?
3. BACnet and OPC UA: adapters only, or our own server API?
4. Final licence policy for industrial modules (Apache-2.0 today).
5. Android/iOS as official targets?
6. Managed fallbacks for platforms without native support — Modbus already has one (the managed client).
7. Gallery distribution: installer, portable, Store/Flatpak/Homebrew?
8. Reference hardware for hardware-in-the-loop tests.

## Definition of Done (per protocol)

Public API documented (XML docs + EN/ID page) · unit + conformance + fuzz (if Rust) tests · simulator · console sample ·
notebook pair · Gallery demo (when relevant) · basic benchmark · entry in the support matrix.
