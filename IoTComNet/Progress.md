# IoTCom.Net — Progress

Development tracking for [PLAN.md](PLAN.md). Update this file whenever a component changes status.
Built by Gravicode Studios, led by Kang Fadhil.

**Current version:** `0.16.0-preview.1` · **Last update:** 2026-10-09

## Snapshot

| Area | Status | Evidence |
|---|---|---|
| .NET solution (`IoTCom.Net.slnx`) | ✅ builds clean, warnings as errors on libraries | `dotnet build IoTCom.Net.slnx` |
| .NET tests | ✅ 596 passing | `dotnet test tests/IoTCom.Net.Tests` |
| Rust workspace | ✅ 43 tests passing, clippy `-D warnings` clean; 10 cargo-fuzz targets (≈ 22 M local runs, no findings) | `cargo test --workspace`, `cargo clippy` |
| Cross-language conformance | ✅ CRC (115 vectors), COBS, SLIP, Modbus frames, CoAP messages (23), MAVLink frames (10), LoRaWAN frames (16, AES/CMAC reference checked against FIPS-197 and RFC 4493), DLMS HDLC + A-XDR (28), M-Bus frames and records (11), CANopen (71), J1939 (28), IEC 104 (53), NTP (25), NDEF (30, C# only), LwM2M TLV (19, C# only) and CRC_EXTRA of all 235 common messages shared by C# and Rust; Rust engine ≡ managed framing | `conformance/`, `NativeModbusTests` |
| Docs EN/ID | ✅ 50 + 50 pages, parity and links verified | `python build/check_docs_parity.py` |
| Notebooks | ✅ 23 EN/ID pairs, code identical, all executed | `python build/check_notebooks.py` |
| Screenshots | ✅ Gallery (headless Skia), dashboard (Edge/CDP), CLI | `docs/images/` |
| NuGet packages | ✅ 41 packages (+ symbol packages) per release (latest `0.20.0-preview.1` with Protocols.Nfc; `0.21.0-preview.1` adds Protocols.Lwm2m); Native.Modbus carries 9 RIDs | release run on tag `iotcomnet-v*` |

## Components

| Component | Status | Notes |
|---|---|---|
| IoTCom.Net.Abstractions | ✅ | endpoints, pub/sub, transport, traffic tap, frame anatomy, exceptions, product info |
| IoTCom.Net.Core | ✅ | pcapng writer + tap (Wireshark-native encapsulation), TCP + in-memory transports, UDP + in-memory datagram network (loss/duplication), StreamTransport, EndpointBase, RecordingTap, HexDump, diagnostics, ReconnectPolicy, NativeLibraryLoader |
| IoTCom.Net.Framing | ✅ | 23 CRC presets (all pass check values), Crc16.Modbus fast path, LRC, SLIP, COBS, HDLC+FCS, LineFraming, pipe helpers |
| IoTCom.Net.Transport.Serial | ✅ | `UseSerial` / `ServeSerial`; hardware test pending (no RS-485 rig in CI) |
| IoTCom.Net.Protocols.Modbus | ✅ | TCP/RTU/ASCII, FC 1–6, 15, 16, 22, 23, 43/14; pipelining; read-only; simulator; anatomy |
| IoTCom.Net.Native.Modbus + Rust `iotcom-modbus` | ✅ win-x64 · ⏳ other RIDs | arm64/Linux/macOS binaries come from the CI native workflow |
| IoTCom.Net.Protocols.Nmea | ✅ | GGA/RMC/GSA/GSV/VTG/GLL/ZDA, GnssState, reader, server, simulator, AIS decoder/encoder/tracker/simulator |
| IoTCom.Net.Protocols.Dmx | ✅ | Art-Net ArtDmx/ArtPoll/ArtPollReply/ArtSync, sACN data + sequence rules, DmxUniverse · RDM ⏳ |
| IoTCom.Net.Adapters.Mqtt | ✅ | MQTTnet 5 adapter, reconnect + resubscribe, JSON/SenML helpers, embedded broker, binary retained will + `AbortAsync` |
| IoTCom.Net.Serialization.SenML | ✅ | JSON + CBOR, resolution (RFC 8428 §4.6), builder, `SenMLPayloadCodec` |
| IoTCom.Net.Serialization.Protobuf / .MessagePack / .Tlv | ✅ | `IPayloadCodec<T>` adapters over Google.Protobuf and MessagePack-CSharp with schema-less views; simple TLV and BER-TLV |
| IoTCom.Net.Protocols.Mdns | ✅ | DNS codec, responder (known-answer suppression, goodbyes), browser (TTL cache, type enumeration), plant simulator · probing, IPv6 ⏳ |
| IoTCom.Net.Protocols.Sparkplug | ✅ | payload codec + topics, edge node (NDEATH will, aliases, rebirth, guarded writes), host application (STATE, gaps, late join), line simulator · data sets, templates, offline buffering ⏳ |
| IoTCom.Net.Transport.Can | ✅ | ICanBus, SocketCAN (libc P/Invoke, CAN FD), slcan over serial/TCP (+ adapter emulator), virtual bus; `gsusb:`/`pcan:` via Transport.Can.Adapters · Kvaser/Vector ⏳ |
| IoTCom.Net.Transport.Can.Adapters | ✅ | gs_usb/candleLight over Transport.Usb (host protocol, bit timing, TX echo, FD when supported), PEAK PCAN-USB via PCANBasic (classic), virtual candleLight · hardware-in-the-loop run on real adapters ⏳ |
| IoTCom.Net.Protocols.CanOpen + Rust `iotcom-canopen` | ✅ | master (NMT, heartbeat consumer, SDO client, SYNC, PDO/EMCY, scan, read-only), device (SDO server, TPDO/RPDO, heartbeat, EMCY), object dictionary, I/O module simulator; fuzzed codec twin + 71 vectors · SDO block, LSS, EDS import ⏳ |
| IoTCom.Net.Protocols.J1939 + Rust `iotcom-j1939` | ✅ | node (address claim with NAME arbitration, BAM + RTS/CTS up to 1785 bytes, requests/responders, listen-only, read-only), SPN decode/encode for common engine PGNs, DM1/DM2, engine ECU simulator; fuzzed codec twin + 28 vectors · ETP, DM3/DM11, NMEA 2000 fast packet ⏳ |
| IoTCom.Net.Protocols.Iec104 + Rust `iotcom-iec104` | ✅ | controlling station (interrogations, read, commands/set points with SBO, clock sync, read-only default), controlled station (point table, spontaneous time-tagged data, command handlers, negative answers), APCI windows and timers, feeder bay simulator; fuzzed codec twin + 53 vectors · file transfer, IEC 62351, 101 serial ⏳ |
| IoTCom.Net.Protocols.Ntp + Rust `iotcom-ntp` | ✅ | SNTP client (RFC 4330 checks, kiss-o'-death, median of servers, replaceable clock), NTP server (rate limiting), era-aware timestamps, drifting clock; fuzzed codec twin + 25 vectors; verified against public servers · NTS, symmetric/broadcast modes, clock filter ⏳ |
| IoTCom.Net.Protocols.Nfc | ✅ | NDEF codec (Text, URI, Smart Poster, MIME, external, AAR, Wi-Fi, chunked), Type 2 tag memory and page map, guarded tear-safe writes, PC/SC on Windows/Linux/macOS (verified listing real readers on Windows), virtual reader; 30 vectors · MIFARE Classic, Type 4/5 tags, NTAG password, a real tag on real hardware ⏳ |
| IoTCom.Net.Protocols.Lwm2m | ✅ | client (registration lifecycle, Read/Discover/Write/Execute/Observe with pmin/pmax) and server (registrations with expiry, device management, read-only default) on IoTCom CoAP; TLV, text, opaque, SenML JSON/CBOR; street light simulator; 19 TLV vectors · DTLS, bootstrap, Create/Delete, Send, block-wise ⏳ |
| IoTCom.Net.Transport.Ble + Rust `iotcom-ble-native` | ✅ | central (scan, GATT read/write/notify, read-only), advertising/iBeacon/Eddystone/GATT codecs, virtual radio; native on btleplug (verified with real WinRT advertisements) · peripheral role, pairing, L2CAP ⏳ |
| IoTCom.Net.Transport.Usb + Rust `iotcom-usb-native` | ✅ | control/bulk/interrupt (nusb), HID reports (hidapi), bulk byte-stream transport, HID relay boards, virtual bus; verified enumerating real devices on Windows · isochronous, hotplug, gadget role ⏳ |
| IoTCom.Net.Protocols.IsoTp + Rust `iotcom-isotp` | ✅ | ISO 15765-2 classic + FD, fuzzed; native `iotcom_isotp` (ABI 1) |
| IoTCom.Net.Protocols.Uds | ✅ | UDS tester (read-only mode), OBD-II scan tool, ECU simulator with vehicle model · flashing helpers, DoIP, J1939 ⏳ |
| IoTCom.Net.Protocols.Mavlink + generator + Rust `iotcom-mavlink` | ✅ | common dialect (235 msgs) via Roslyn generator, custom dialects, v1/v2 + signing, UDP/TCP/serial links, GCS helper, quadcopter simulator · mission protocol, FTP, routing ⏳ |
| IoTCom.Net.Protocols.Coap + Rust `iotcom-coap` | ✅ | client/server, CON/NON reliability, dedup, separate responses, Observe, Block1/Block2, link-format, SenML, greenhouse simulator · DTLS/OSCORE/TCP ⏳ |
| IoTCom.Net.Protocols.LoRaWan + Rust `iotcom-lorawan` | ✅ | 1.0.x codec + crypto, OTAA/ABP, MAC commands, EU868/US915/AS923-2 + airtime, Semtech UDP (forwarder + server), light network server, Class A device MAC, Cayenne LPP, simulator · Class B/C, ADR, Basics Station ⏳ |
| IoTCom.Net.Protocols.Dlms + Rust `iotcom-dlms` | ✅ | HDLC + wrapper, A-XDR, OBIS, LLS/HLS-GMAC + suite 0 ciphering, GET blocks + selective access, SET/ACTION, client (read-only default), server + COSEM classes, meter simulator · suites 1/2, push ⏳ |
| IoTCom.Net.Protocols.MBus + Rust `iotcom-mbus` | ✅ | frames, variable data records (DIF/VIF, BCD, dates), master (scan, secondary addressing), slave simulator · wireless M-Bus/OMS ⏳ |
| IoTCom.Net.Protocols.AtCommand | ✅ | URC-aware parser, modem client (SMS, info, read-only), LTE-M simulator · PDU SMS, CMUX ⏳ |
| IoTCom.Net.Protocols.Astm | ✅ | E1394 records, E1381 link (NAK retransmission), receiver/sender, analyzer simulator, HL7 bridge · host query ⏳ |
| IoTCom.Net.Protocols.Hl7 | ✅ | ER7 codec + escaping, MLLP server/client with ACK matching, LOINC vitals, PatientMonitorSimulator (ASTM in its own package) |
| IoTCom.Net.Adapters.Dicom | ✅ | fo-dicom 5.2.6 Storage SCP/SCU, C-ECHO, windowed renderer → PNG, synthetic CT/MR/X-ray with planted findings (not trimmable/AOT) |
| IoTCom.Net.Adapters.OpcUa | ✅ | OPC Foundation stack 1.5.378: client (security, user name, browse/read/write/call, subscriptions, read-only) + plant simulator server (None, Basic256Sha256) · events/A&C, history, complex types ⏳ |
| IoTCom.Samples.Medical (sample, not packed) | ✅ | NEWS2 (RCP 2017), trends, anomalies, OpenAI-compatible AI client, SBAR + imaging pre-read with ground-truth scoring |
| IoTCom.Net.Hosting | ✅ | AddIoTCom, IoTComEndpoints, keyed services, shared tap, health checks |
| IoTCom.Net (meta) | ✅ | protocol-specific hosting extensions |
| CLI `iotcom` (IoTCom.Net.Cli) | ✅ | Spectre.Console UI with the frame lane; `sniff tcp/udp/can` with pcapng |
| Templates | ✅ | iotcom-console (modbus/nmea/mqtt × en/id), iotcom-worker |
| Gallery (Avalonia) | ✅ | 23 demos (LPWAN & city: street lights over LwM2M; Workbench: NFC asset tags; Messaging: fleet clock sync over NTP; Energy & grid: substation control over IEC 104; Automotive: truck cluster over J1939; Industrial: CANopen I/O modules; Workbench: USB bench; Building: nearby Bluetooth devices; Industrial: OPC UA tag browser; Messaging: plant network with mDNS + Sparkplug B; Navigation: harbour traffic over AIS; LPWAN & metering: LoRaWAN network monitor, smart meter reading; Navigation: MAVLink drone; Building: CoAP greenhouse; Automotive: vehicle diagnostics; Medical: ICU bedside monitors, imaging AI pre-read), Run/Code/Docs/Traffic, EN/ID runtime switch, light/dark, headless screenshot tool |
| IoTCom.Gateway web sample | ✅ | Modbus → MQTT (SenML), REST + SSE, HMI dashboard (EN/ID, light/dark, mobile) |
| Console samples | ✅ | ModbusMaster, ModbusSlaveSimulator, NmeaGpsReader, ArtNetPlayer, MqttSenMLBridge, Hl7MllpListener, UdsTester, CoapObserve, MavlinkTelemetry, LoRaWanGatewayMonitor, DlmsMeterReader, MBusScanner, AstmAnalyzerBridge, MdnsDiscovery, SparkplugEdgeNode, OpcUaBrowser, BleHeartRate, UsbRelay, CanOpenMaster, J1939Monitor, Iec104Scada, NtpClock, NfcTagReader, Lwm2mClient |
| Benchmarks | ✅ | BenchmarkDotNet (short job, this machine): Modbus request round trip 5.0 µs sequential / 2.2 µs with 16 in flight over the in-memory transport (≈ 200k–450k req/s; design target ≥ 20k req/s on TCP loopback); CRC ≈ 2.2 ns/byte, zero allocations; LoRaWAN ≈ 5 µs per uplink (encrypt + MIC, or decode + verify + decrypt) |
| CI | ✅ defined | repo root `.github/workflows/iotcomnet-ci.yml`, `iotcomnet-native.yml`, `iotcomnet-release.yml` (release on tag `iotcomnet-v*`, pushes with the `NUGET_API_KEY` secret) |
| VS Code extension | ✅ v0.1 in 0.7.0-preview.1 (frame viewer, traffic monitor, protocols/devices views, snippets; `iotcom rpc`) | Phase 1 |

## Decisions taken during Phase 0

- **Modbus has two engines.** The managed `ModbusClient` is the default (works on every RID, AOT); the Rust engine
  (`NativeModbusClient`) is a drop-in `IModbusClient`. This answers open question 6 (managed fallback) for Modbus.
- **One DMX package.** Art-Net and sACN share the universe model and node plumbing, so they live in
  `IoTCom.Net.Protocols.Dmx` rather than two packages.
- **C# bindings are hand-written for ABI v1** (5 exported functions + 2 structs) and checked by the cross-language
  test; generated bindings (csbindgen) arrive with the next native crate.
- **Gallery screenshots are rendered headlessly** from the real window, so docs images stay reproducible in CI.

## Decisions taken in 0.21

- **LwM2M sits on IoTCom.Net's own CoAP stack** through `InternalsVisibleTo`, so a client registers and answers the
  server's requests on the same socket (what NAT traversal needs) without a second public CoAP API.
- **Observations route their own token.** The first version let the request/response helper register the same token
  and remove it when the exchange ended, which silently dropped every notification; the session test caught it.
- **The LwM2M server is read-only by default**, like the other masters; the Gallery puts a write lock in front.

## Decisions taken in 0.20

- **NFC is C# only**, as the design table says: PC/SC is an operating-system API wrapped with `LibraryImport`
  (three signature sets, because pcsc-lite uses 64-bit `long` on Linux), and NDEF is a small codec checked by Python
  vectors. There is no Rust twin.
- **Writes are tear-safe and never permanent.** The NDEF TLV is emptied first and its header written last, pages 0–3
  are never written, and the library never locks a tag.
- **`PcscNfcReader.Open()` skips contact readers and virtual smart cards** (this machine lists a Broadcom contact reader
  and a Windows Hello card); a name match is required to use anything else.

## Decisions taken in 0.19

- **EtherNet/IP was deferred.** Work on it was interrupted twice, so the next items that do not operate equipment came
  first: NTP/SNTP.
- **SNTP measures; it never sets the system clock.** Applications discipline their own clock through the replaceable
  `Clock`, and the OS time service stays in charge of the machine.
- **The in-memory network can now be slow** (`Latency`, `Jitter`), because offset and delay only mean something when
  packets take time; the defaults keep every existing test unchanged.

## Decisions taken in 0.18

- **IEC 104 is managed C# with a Rust codec twin**, like CANopen and J1939: the link layer is mostly timers and windows
  that belong next to the TCP pipe, while the binary codec is what benefits from fuzzing.
- **ASDUs are dispatched off the read loop.** A station answering an interrogation with more ASDUs than the k window
  must keep receiving acknowledgements while it sends; handling inside the read loop deadlocked by design.
- **The client is read-only by default** (commands, set points, clock sync), like the DLMS and CANopen masters; the
  Gallery opens a second, command-capable connection only when the operator unlocks control.

## Decisions taken in 0.17

- **J1939 also follows the codec-twin pattern.** The transport protocol is a small per-connection state machine whose
  timeouts sit next to the address claim, so it stays managed with the node; the Rust crate mirrors the codec for
  fuzzing and conformance.
- **Listen-only means silent.** A listen-only node does not claim an address, refuses requests and still reassembles
  RTS/CTS transfers between other nodes, so a monitor on a moving vehicle sees VINs and DM1s without transmitting.
- **Read-only nodes may only request.** `SendAsync` is refused, so tools built on the node cannot send commands
  (TSC1 and similar).

## Decisions taken in 0.16

- **CANopen follows the codec-twin pattern** (managed C# runtime, fuzzed Rust codec, shared vectors from Python) rather
  than a native state machine like ISO-TP: SDO and NMT are simple request/response flows whose timing lives in the
  master, and keeping them managed avoids another native library per RID.
- **Event-driven TPDOs are coalesced** for a couple of milliseconds, like a CiA 301 inhibit time, so several objects
  changed together go out in one PDO.
- **The reference caught its own mistake**: 0x7FF was first written as node 127's heartbeat; heartbeats end at 0x77F,
  and the C# and Rust codecs both classified it correctly, so the vector was fixed.

## Decisions taken after 0.15 (tooling)

- **Generated bindings are the reference, not the runtime code.** csbindgen emits `DllImport` with raw pointers; the
  packages keep `LibraryImport` with SafeHandles and `out` parameters, which are safer and AOT-friendly. A reflection
  test compares both (entry points, parameter count, ABI size per parameter, struct size and field offsets), so drift
  fails the build without giving up the ergonomic bindings. The test was checked by deliberately breaking one signature.
- Headers are generated with cbindgen from the crate sources (`rust/include`) and compiled as C99 and C++ in CI;
  `iotcom_common.h` declares the two functions the `export_common!` macro adds, which neither generator expands.

## Decisions taken in 0.15

- **gs_usb is managed C# on top of Transport.Usb** rather than another Rust crate: the protocol is a handful of control
  requests and fixed little-endian frames, so the USB library already in Rust is enough (design §4: no duplicate
  hardware layer).
- **PCAN through PEAK's PCANBasic** (P/Invoke, resolved per OS: PCANBasic.dll, libpcanbasic.so, PCBUSB) because the
  adapter's own protocol is undocumented; the driver package is a prerequisite and missing drivers are reported as
  `PlatformNotSupportedException`.
- **URI schemes are pluggable** (`CanBus.RegisterScheme`) so `Transport.Can` stays free of USB and vendor dependencies.
- Neither adapter has been run against physical hardware in this release; the gs_usb path is verified against the
  Linux driver's frame layout and a protocol-level simulator.

## Decisions taken in 0.14

- **USB in Rust with nusb, HID with hidapi**: nusb is pure Rust (no libusb to ship) on WinUSB, usbfs and IOKit; hidapi
  uses its pure-Rust backends on Windows and Linux (no libudev), so the zig cross builds need no target sysroots.
- **Synchronous C ABI for USB**: every transfer blocks with a timeout on the caller's thread (the .NET side runs it on
  the thread pool behind one semaphore per device). Timeouts are a distinct status (-31) mapped to `null` reads, not
  exceptions.
- **The USB HID relay board is the reference HID device**: cheap, everywhere, and its protocol (feature reports)
  exercises the HID path that keyboards and mice do not.
- **The Gallery never opens real USB devices** and shows a fixed sample list in screenshots, so docs images do not
  publish the rendering machine's hardware and serial numbers.

## Decisions taken in 0.13

- **BLE goes through Rust** (design: hardware access in Rust): btleplug covers WinRT, BlueZ and CoreBluetooth with one
  API. The C ABI stays small and stable by passing asynchronous events as one-line JSON drained by a poll call; GATT
  calls block on the library's own Tokio runtime with timeouts. libdbus is vendored on Linux so zig cross builds link.
- **Native BLE builds are best effort per RID** (`continue-on-error` in the native workflow) so a BlueZ or toolchain
  problem on one target cannot block the Modbus and ISO-TP libraries or a release; the virtual radio works everywhere.
- **Advertisements without properties are still reported**: WinRT can announce a device before its properties are
  readable; dropping those hid weak, rarely advertising devices.

## Decisions taken in 0.12

- **OPC UA stays an adapter** (design §4, ADR-005, open question 3): the OPC Foundation stack is MIT-licensed in
  1.5.378, so wrapping it raises no licence issue. 2.0.0 had been out for two days, so the mature 1.5.378 line is pinned.
- **Certificates live in a directory PKI per application** (`%LOCALAPPDATA%/IoTCom.Net/opcua/pki`), created on first
  use. Untrusted server certificates are refused unless the caller opts in (`AcceptUntrustedCertificates`, CLI
  `--accept-untrusted`); the simulator accepts any client because it is a test tool.
- **The traffic tap shows service calls**, not bytes: the stack owns the secure channel, so frames carry the service
  and its arguments as text.

## Decisions taken in 0.11

- **Sparkplug B is managed C# over the MQTT adapter** (design §4: MQTT is wrapped, not rebuilt). The payload is
  encoded with Google.Protobuf's `CodedOutputStream` and Tahu field numbers instead of generated classes, keeping the
  package trimmable and reflection-free; tests pin a hand-encoded payload.
- **A lost connection is simulated by dropping the socket.** MQTTnet's broker did not publish the will for an MQTT 5
  DISCONNECT with reason 0x04, so `AbortAsync` closes the connection without DISCONNECT — what a pulled cable does.
- **Hosts ask for a rebirth once per node** until its NBIRTH arrives: several devices' DDATA usually precede it when a
  host joins late.
- **The mDNS browser resolves from its cache** as well as from responses: with known-answer suppression a responder
  stays silent about records the browser already holds.
- **Protobuf and MessagePack are optional packages**, outside the meta-package, so their dependencies stay opt-in; TLV
  has none and ships in the meta-package.

## Decisions taken in 0.10

- **AIS lives in the NMEA package**: it arrives as NMEA sentences, so the decoder takes `NmeaSentence`s and the same
  readers and servers carry it. Expected values in tests come from published examples cross-checked by an
  independent decoder (one remembered value was wrong; the independent decode settled it).
- **AT and ASTM are managed C#** (text protocols, design §5). The AT parser is sans-I/O and owns URC separation, the
  part every hand-written modem driver gets wrong; the modem client is read-only on request.
- **ASTM bridges to HL7** rather than inventing a result model: hospitals consume ORU^R01, and the HL7 package
  already validates it.
- **Portable GCM for DLMS.** macOS supports only 16-byte AES-GCM tags; DLMS needs 12. Full tags truncated to 12 bytes
  are standard GCM truncation and are checked byte for byte against native 12-byte GCM on Windows and Linux.

## Decisions taken in 0.9

- **DLMS and M-Bus run in managed C#; the Rust crates are fuzzed codec twins** (HDLC + A-XDR, M-Bus frames +
  records). Meters answer at 2400–9600 baud, so native code would buy nothing; the binary parsers are where fuzzing
  pays off, and they are mirrored exactly.
- **Read-only twice.** `DlmsClient` blocks SET/ACTION unless `ReadOnly = false`, and the server only lets an
  authenticated management client write, as real meters do; the CLI adds `--allow-write` and a confirmation for the relay.
- **The simulator is local.** Two days of history, Indonesian 230 V/50 Hz, PLN WBP/LWBP tariffs and rooftop solar
  export make the profile and tariff registers meaningful in demos and tests.
- **Anchors from the field.** The classic SNRM frame, a published public-client AARQ and the M-Bus documentation's
  water-meter telegram (checksum 0x18) are pinned in the tests and the Python reference.

## Decisions taken in 0.8

- **LoRaWAN runs in managed C#; the Rust crate is a fuzzed twin** (as for CoAP and MAVLink). Uplinks are rare and
  the BCL's AES is AOT-friendly, so a native library would add packaging cost without a speed-up that matters.
  `iotcom-lorawan` uses the RustCrypto `aes` and `cmac` crates instead of its own AES.
- **The conformance reference has its own AES.** `generate.py` implements AES-128 and AES-CMAC in plain Python and
  checks them against FIPS-197 and RFC 4493 (and the published lora-packet frame) before writing vectors, so neither
  engine under test can hide a crypto bug in them.
- **A light network server, not a full one.** It covers what labs and pilots need (OTAA/ABP, dedup, Class A, the
  MAC commands that matter) and keeps the Semtech UDP gateway side reusable; production fleets keep ChirpStack/TTS,
  which `iotcom lorawan simulate` can drive.
- **AS923-2 is a first-class region** (Indonesia), alongside EU868 and US915.

## Decisions taken in 0.5

- **The dialect is generated, not hand-written.** A Roslyn source generator turns the official MAVLink XML into
  classes at compile time. The library ships the common dialect, and the same generator, inside the package, compiles
  application dialects. This is the design's "C# source generator" item, and it keeps CRC_EXTRA exact for all 235
  messages, which is checked against an independent reference.
- **The runtime is managed and the Rust crate is a fuzzed twin** (same reasoning as CoAP): a per-frame FFI call would
  cost more than the framing. `iotcom-mavlink` gives the Rust side a verified frame codec for embedded companions.

## Decisions taken in 0.4

- **CoAP runs in managed C#; the Rust crate is a fuzzed twin of the codec.** The design names CoAP as "Rust codec + C#
  I/O". Calling across the FFI for every datagram would cost more than the codec itself, so the runtime path stays
  managed (AOT, every RID, no native library). `iotcom-coap` keeps the Rust side honest through
  `/conformance/coap.json` and fuzzing, and is ready for embedded or WASM use.
- **Datagram transports are shared.** `IDatagramTransport` (UDP plus the lossy in-memory network) is in Core, so
  MAVLink, LwM2M and future UDP protocols reuse it, along with the loss simulation used in tests and the Gallery.

## Decisions taken in 0.3

- **SocketCAN in C#, not Rust.** The design proposed the `socketcan` crate (pattern B). Opening a `CAN_RAW` socket is
  six libc calls, so a `LibraryImport` binding avoids shipping a second native library for one OS. It stays
  AOT-friendly and still runs a dedicated receive thread. Rust remains the home of the complex state machines.
- **ISO-TP in Rust, UDS in C#.** ISO-TP is the binary state machine worth fuzzing. UDS is request/response semantics
  where C# async timeouts (P2/P2*) are the natural fit.
- **slcan first** as the USB adapter (open question 1): it is driver-free on every OS, cheap (CANable), and fully
  testable via the adapter emulator. PCAN/Kvaser/gs_usb follow.

## AI evaluation (medical demos, real models)

| Task | Model | Result |
|---|---|---|
| SBAR note from vitals snapshot | Azure OpenAI `gpt-5-mini` | coherent, recognises the sepsis pattern, ≈ 5 s |
| Image pre-read, 12 synthetic modality/finding pairs | Azure OpenAI vision-capable GPT-5 deployment | 10/12 match the planted finding; misses: subtle CT pneumothorax (called normal), X-ray consolidation (called a mass) |

The phantoms are schematic, so these numbers test the pipeline, not clinical accuracy.

## Known gaps

- Native binaries for Linux, macOS and Windows ARM64 are not built on this machine (the MSVC ARM64 tools and cross
  toolchains are not installed). The workflow `iotcomnet-native.yml` builds them.
- Serial and Art-Net broadcast paths are verified on loopback/in-memory only; no hardware-in-the-loop rig yet.

## Log

| Date | Change |
|---|---|
| 2026-10-10 | OMA LwM2M (`0.21.0-preview.1`): client and server on IoTCom CoAP, TLV/SenML, observe with pmin/pmax, street light simulator, 19 TLV vectors, CLI `lwm2m`, Gallery street lights, sample, notebook pair, docs. Three timing-dependent CI tests fixed. |
| 2026-10-09 | NFC/NDEF (`0.20.0-preview.1`): NDEF codec, Type 2 tags, PC/SC on three OSes, virtual reader, 30 conformance vectors, CLI `nfc`, Gallery asset tags, sample, notebook pair, docs. Sparkplug test made deterministic. |
| 2026-10-09 | NTP/SNTP (`0.19.0-preview.1`): SNTP client, server, era-aware timestamps, drifting clock, in-memory latency, fuzzed Rust twin, 25 conformance vectors, CLI `ntp`, Gallery fleet clock sync, sample, notebook pair, docs. IEC 104 CP56 year fix. EtherNet/IP deferred. |
| 2026-10-09 | IEC 60870-5-104 (`0.18.0-preview.1`): controlling and controlled station, APCI windows/timers, feeder bay simulator, fuzzed Rust twin, 53 conformance vectors, CLI `iec104`, Gallery substation mimic (new Energy & grid category), sample, notebook pair, docs. |
| 2026-10-09 | SAE J1939 (`0.17.0-preview.1`): node with address claim and transport protocol, SPNs, DM1, engine simulator, fuzzed Rust twin, 28 conformance vectors, CLI `j1939`, Gallery truck cluster, sample, notebook pair, docs. |
| 2026-10-09 | CANopen (`0.16.0-preview.1`): master + device + simulator, fuzzed Rust twin, 71 conformance vectors, CLI `canopen`, Gallery I/O modules, sample, notebook pair, docs. First Phase 2 item. |
| 2026-10-09 | Native bindings tooling: `iotcom-bindgen` (cbindgen headers + csbindgen reference for modbus, isotp, ble, usb), `BindingDriftTests`, CI drift and header compile gate. Phase 1 of PLAN complete. |
| 2026-10-09 | USB CAN adapters (`0.15.0-preview.1`): gs_usb/candleLight and PCAN-USB backends with `gsusb:`/`pcan:` URIs, virtual candleLight, docs and notebook section. 0.14.0 published with `iotcom_usb` for all 9 RIDs. |
| 2026-10-09 | USB and HID (`0.14.0-preview.1`): Transport.Usb with Rust `iotcom_usb` (nusb, hidapi), relay boards, virtual bus, CLI `usb`, Gallery USB bench, UsbRelay sample, notebook pair, docs. 0.13.0 published with `iotcom_ble` built for all 9 RIDs. |
| 2026-10-09 | Bluetooth LE (`0.13.0-preview.1`): Transport.Ble with Rust `iotcom_ble` (btleplug), codecs, virtual radio, CLI `ble`, Gallery radar demo, BleHeartRate sample, notebook pair, docs. |
| 2026-10-09 | OPC UA (`0.12.0-preview.1`): adapter package with client and plant simulator server (secure sessions verified in tests), CLI `opcua`, Gallery tag browser, OpcUaBrowser sample, notebook pair, docs. |
| 2026-10-09 | Plant networks (`0.11.0-preview.1`): mDNS/DNS-SD, Sparkplug B edge node + host, Protobuf/MessagePack/TLV codecs with `IPayloadCodec<T>`, MQTT binary will, CLI `mdns`/`sparkplug`/`payload`, Gallery plant-network demo, two samples, notebook pair, three docs pages. |
| 2026-10-09 | Field devices (`0.10.0-preview.1`): AIS, AT commands, ASTM + HL7 bridge, Gallery harbour demo, notebooks, docs. 0.9.0 was tagged but not published (macOS runner never started); its CI also exposed macOS's 16-byte-only AES-GCM, fixed with portable 12-byte tags. |
| 2026-10-09 | Metering (`0.9.0-preview.1`): DLMS/COSEM and M-Bus packages with simulators, Rust twins + fuzz targets 8–9 (≈ 1.5 M runs, no findings), CLI, RPC monitors, Gallery smart-meter demo, samples, notebook pair, docs. Fixed during testing: server replies went to SAP 16 even for the management client (peer address now learned per frame). |
| 2026-10-08 | LoRaWAN (`0.8.0-preview.1`): package (codec, crypto, Semtech UDP, network server, device MAC, simulator), Rust twin + 7th fuzz target (2.7 M runs, no findings), LoRaTap pcapng, CLI `lorawan`, RPC decoders/monitors, Gallery radio map, sample, notebook pair, docs. Found while testing: replays were reported as MIC failures (FCnt epoch), real forwarders' unpadded base64 was rejected, LPP GPS altitude scale. |
| 2026-10-08 | Editor tooling (`0.7.0-preview.1`): CLI `iotcom rpc` (JSON-RPC over stdio) and the VS Code extension v0.1 driving it; extension tests run against the real CLI in CI and the `.vsix` is an artifact. Docs page with screenshots rendered from the real webviews. |
| 2026-10-08 | Capture (`0.6.0-preview.1`): pcapng writer/tap (verified with scapy locally, tshark in CI), `iotcom sniff tcp/udp/can`, Gallery Save .pcapng. Fixed: NativeAOT publish reached the MAVLink generator (NETSDK1207); CI green again. |
| 2026-10-05 | Published 0.5.0-preview.1 (MAVLink); first attempt failed on a Release-only analyzer rule (CA1868), fixed and re-tagged before anything reached NuGet. |
| 2026-10-05 | MAVLink (`0.5.0-preview.1`): package with the generated common dialect, Roslyn generator for custom dialects, signing, GCS helper, quadcopter simulator, Rust frame twin + fuzz target, Gallery drone demo, CLI `mavlink`, MavlinkTelemetry sample, notebook pair, docs. Fixed during the Gallery review: concurrent senders could reorder sequence numbers (false loss) — send is now atomic, covered by a test. |
| 2026-10-05 | Published 0.4.0-preview.1 (CoAP). |
| 2026-10-05 | CoAP (`0.4.0-preview.1`): CoAP package + Rust codec twin, datagram transports, Gallery greenhouse demo, CLI `coap`, CoapObserve sample, notebook pair, docs. Fixed during smoke test: null option setters added an empty value (now covered by a test). |
| 2026-10-05 | Published 0.3.0-preview.1 (automotive); CI, fuzz, native and release workflows green. |
| 2026-10-05 | Automotive (`0.3.0-preview.1`): CAN transport, ISO-TP (Rust), UDS/OBD-II + ECU simulator, cargo-fuzz in CI, Gallery vehicle diagnostics, CLI `can`/`uds`/`obd`, UdsTester sample, notebook pair, CAN/UDS docs. |
| 2026-10-05 | Published 0.2.0-preview.1 (healthcare) via tag `iotcomnet-v0.2.0-preview.1`. |
| 2026-10-05 | Healthcare (`0.2.0-preview.1`): HL7 v2/MLLP package, DICOM adapter, Gallery medical demos with real LLM/vision tests, CLI `hl7`/`dicom`, Hl7MllpListener sample, notebook pair, 3 new EN/ID docs pages. |
| 2026-10-05 | Published 0.1.0-preview.1 to nuget.org (all CI jobs green on Windows, Linux, macOS, Alpine; natives for 9 RIDs). |
| 2026-10-05 | First CI run: fixed a start/stop race in DMX, NMEA and Modbus server loops (token read lazily from a field; caught on Alpine) and musl `cdylib` output (`-crt-static`). |
| 2026-10-05 | Moved into the `Vibe_IoT` monorepo (`IoTComNet/`); workflows scoped to this folder. NativeAOT: trim/AOT analysis of a published sample is warning-free (local native link needs the VS developer environment; CI covers it on Linux). |
| 2026-10-05 | Phase 0 delivered as `0.1.0-preview.1`: core libraries, Modbus (C# + Rust), NMEA, Art-Net/sACN, MQTT, SenML, hosting, CLI, Gallery, gateway sample, console samples, templates, notebooks, EN/ID docs, CI definitions, NuGet icon. |
| 2026-10-04 | Solution design approved (solution-design.md). |
