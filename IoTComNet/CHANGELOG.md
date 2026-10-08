# Changelog

All notable changes to IoTCom.Net. Versions follow SemVer; the native ABI version is tracked separately
(`iotcom_abi_version()`). *Bahasa Indonesia di bawah setiap rilis.*

## 0.7.0-preview.1 — 2026-10-08

Editor tooling.

- **VS Code extension v0.1 (`tools/vscode-iotcom`, IoTCom.Net Tools):** frame viewer (decode pasted or selected bytes
  into a frame lane with a field table), traffic monitor (live decoded frames from simulators, CAN interfaces or a
  MAVLink UDP port, filter, Save .pcapng), protocols view (package, docs EN/ID, notebook, sample), devices view and
  C# snippets. English and Bahasa Indonesia. CI builds the CLI, tests the extension against it and publishes the
  `.vsix` as an artifact.
- **CLI `iotcom rpc`:** JSON-RPC 2.0 over stdio (`initialize`, `decode`, `devices`, `monitor.start/stop/save`,
  `shutdown`, `frame` notifications) for editors and other tools; the extension never duplicates protocol logic.
- **Docs:** new page *Tools → VS Code extension*.

*Tooling editor: ekstensi VS Code v0.1 (penampil frame, pemantau lalu lintas dengan Simpan .pcapng, tampilan
protokol, snippet; EN/ID) dan perintah `iotcom rpc` (JSON-RPC lewat stdio) yang dipakainya.*

## 0.6.0-preview.1 — 2026-10-08

Capture and sniffing.

- **pcapng export (Core):** `PcapngWriter` and `PcapngTap` (an `ITrafficTap`), flushed per packet so a capture
  survives an abrupt stop. Wireshark dissects the protocols natively:
  - CAN as `LINKTYPE_CAN_SOCKETCAN`;
  - Modbus/TCP, NMEA and HL7 inside synthetic IPv4 + TCP (consistent sequence numbers, valid checksums);
  - CoAP, Art-Net, sACN and MAVLink inside IPv4 + UDP;
  - everything else as user data, with the decoded summary as the packet comment.
  CI checks the encapsulation with `tshark` on Linux.
- **CLI `iotcom sniff`:** `tcp` and `udp` transparent relays that decode both directions (Modbus/TCP, HL7/MLLP, CoAP,
  MAVLink, raw), plus a passive `can` capture. All three print one line or a frame lane per frame and write pcapng
  with `--pcap`.
- **Gallery:** the Traffic tab gets **Save .pcapng**.
- **Fix:** a NativeAOT publish (`-p:PublishAot=true -r <rid>`) no longer reaches the netstandard2.0 MAVLink generator
  (NETSDK1207); the generator treats those properties as local.

*Capture: ekspor pcapng yang diurai langsung oleh Wireshark (CAN, Modbus/TCP, CoAP, …), perintah `iotcom sniff`
(relay TCP/UDP transparan dan capture CAN), tombol Simpan .pcapng di Galeri, dan perbaikan publish NativeAOT.*

## 0.5.0-preview.1 — 2026-10-05

MAVLink, with a source generator for dialects.

- **New package `IoTCom.Net.Protocols.Mavlink`** (also in the meta-package):
  - The complete common dialect (235 messages and all enums) generated from the official MAVLink XML (MIT).
  - Frame codec: v1/v2, MAVLink 2 truncation, signing (SHA-256, 48-bit timestamps), and a streaming parser that
    resynchronises after garbage.
  - `MavlinkConnection` over UDP (ground-station or vehicle style), TCP, serial or in-memory, with peers, loss from
    sequence gaps and heartbeats.
  - `MavlinkGroundStation`: COMMAND_LONG → COMMAND_ACK with retries, arm/takeoff/land/RTL, a complete parameter
    download on lossy links, `PARAM_SET` confirmation and read-only mode.
  - `MavlinkVehicleSimulator`: an ArduCopter-like quadcopter with pre-arm checks.
- **Roslyn source generator** `IoTCom.Net.Protocols.Mavlink.Generator`, shipped in the package (`analyzers/dotnet/cs`):
  - Compile your own dialect XML (`AdditionalFiles`, `MavlinkNamespace`, `MavlinkDialectName`); the official XML is
    available through `$(MavlinkDialectsPath)`.
  - `CompositeDialect` combines dialects. Diagnostics MAV001/MAV002 report invalid XML and missing includes.
- **Rust crate `iotcom-mavlink`:** a frame-codec twin, fuzzed. `/conformance/mavlink*.json` come from an independent
  Python reference that reproduces the published CRC_EXTRA constants.
- **Gallery:** *Drone telemetry over MAVLink* — artificial horizon, track map, battery, status texts and guarded
  commands.
- **CLI:** `iotcom mavlink listen|simulate|cmd|params|decode`.
- **Samples, notebook, docs:** `MavlinkTelemetry` sample; notebook pair `navigation/08-mavlink`; MAVLink page (EN/ID);
  NOTICE now credits fo-dicom and the MAVLink definitions.

*MAVLink: paket baru dengan dialek common lengkap dari XML resmi lewat source generator Roslyn (bisa untuk dialek Anda
sendiri), signing, koneksi UDP/TCP/serial, helper ground station, simulator quadcopter, codec frame Rust yang di-fuzz,
demo Galeri dengan artificial horizon, perintah CLI `mavlink`, sampel, notebook, dan dokumentasi dua bahasa.*

## 0.4.0-preview.1 — 2026-10-05

CoAP, and a shared datagram transport.

- **New package `IoTCom.Net.Protocols.Coap`** (also in the meta-package):
  - Client and server over UDP: confirmable retransmission with exponential back-off, deduplication with a reply
    cache, separate responses, and RST for unknown tokens.
  - Observe (RFC 7641) with freshness checks and deregistration.
  - Block-wise transfers (RFC 7959) in both directions, plus `/.well-known/core` discovery with filters (RFC 6690).
  - SenML content formats, a read-only client mode, `AddCoapClient` / `AddCoapServer` hosting, and a greenhouse
    device simulator.
- **Rust crate `iotcom-coap`:** the same codec, fuzzed, and kept byte-for-byte in sync with C# by
  `/conformance/coap.json` (including the RFC 7252 examples and malformed messages).
- **Core:** `IDatagramTransport` with `UdpDatagramTransport` and `InMemoryDatagramNetwork` (configurable loss and
  duplication), and the shared `UseUdp` / `UseInMemory` builder extensions.
- **Gallery:** *Smart greenhouse over CoAP* — observed sensors, actuators, a Block2 log, a separate response, and a
  packet-loss slider with a live message sequence chart.
- **CLI:** `iotcom coap get|put|observe|discover|ping|serve`; `iotcom info` now lists CAN, UDS and CoAP.
- **Samples, notebook, docs:** `CoapObserve` sample; notebook pair `messaging/07-coap`; CoAP page (EN/ID).

*CoAP: paket client/server baru (Observe, Block-wise, penemuan, simulator rumah kaca), codec Rust yang di-fuzz dengan
vektor bersama, transport datagram (UDP + jaringan memori dengan kehilangan paket), demo Galeri dengan diagram urutan
pesan, perintah CLI `coap`, sampel, notebook, dan dokumentasi dua bahasa.*

## 0.3.0-preview.1 — 2026-10-05

Automotive: CAN, ISO-TP, UDS and OBD-II, plus fuzzing in CI.

- **New package `IoTCom.Net.Transport.Can`** (also in the meta-package):
  - `ICanBus` with filtered readers, the traffic tap and metrics.
  - Backends: Linux SocketCAN (libc P/Invoke, CAN FD), slcan/Lawicel USB adapters over serial or TCP (CAN FD
    extension), and an in-process virtual bus.
  - candump notation, an slcan adapter emulator, and `AddCanBus` hosting.
- **New package `IoTCom.Net.Protocols.IsoTp`:**
  - ISO 15765-2 as the sans-I/O Rust crate `iotcom-isotp` (native `iotcom_isotp`, ABI 1).
  - Classic CAN and CAN FD, block size / STmin / WAIT / overflow, N_Bs / N_Cr, extended addressing, 32-bit FF_DL.
- **New package `IoTCom.Net.Protocols.Uds`:**
  - UDS tester: sessions, security access, DIDs, DTCs, routines, response-pending / busy handling, read-only mode.
  - OBD-II scan tool: mode 01 PIDs, 03 / 07 / 04, VIN.
  - ECU simulator with a vehicle model and fault injection.
- **Fuzzing:** `cargo-fuzz` targets for the Modbus decoder, the Modbus master, the PDU parsers and ISO-TP. The
  `iotcomnet-fuzz.yml` workflow runs them on every Rust change and nightly.
- **Gallery:** *Vehicle diagnostics* demo (tachometer and tell-tales, live OBD-II, DTCs with MIL, security access,
  guarded write). The Traffic tab decodes CAN, UDS and OBD frames.
- **CLI:** `iotcom can list|dump|send|simulate`, `iotcom uds read|dtc|raw`, `iotcom obd live|vin|dtc`. `--can sim`
  starts a built-in ECU.
- **Samples, notebook, docs:** `UdsTester` console sample; notebook pair `automotive/06-can-uds`; CAN and UDS pages
  (EN/ID).

*Otomotif: paket baru CAN/CAN FD (SocketCAN, slcan, bus virtual), ISO-TP sebagai state machine Rust, tester UDS, scan
tool OBD-II, dan simulator ECU; target cargo-fuzz di CI; demo Galeri diagnostik kendaraan; perintah CLI `can` / `uds` /
`obd`; sampel, notebook, dan dokumentasi dua bahasa.*

## 0.2.0-preview.1 — 2026-10-05

Healthcare: HL7 v2 and DICOM, plus medical demos with AI.

- **New package `IoTCom.Net.Protocols.Hl7`:**
  - ER7 parser and builder: delimiters, escaping, repetitions and sub-components, terser-style paths.
  - MLLP framing; `Hl7MllpServer` (auto-ACK AA/AR, custom ACKs, subscriptions) and `Hl7MllpClient` (waits for the
    ACK matching MSA-2).
  - LOINC vital-sign helpers and `PatientMonitorSimulator` (stable, sepsis, hypoxia, hypertension).
  - Also in the `IoTCom.Net` meta-package, with `AddHl7Server` / `AddHl7Client`.
- **New package `IoTCom.Net.Adapters.Dicom`:**
  - Storage SCP/SCU (C-STORE, C-ECHO) over fo-dicom in the endpoint model.
  - Windowed grayscale renderer to PNG.
  - Synthetic CT/MR/X-ray studies with planted ground-truth findings.
- **Gallery:** new *Medical & healthcare* category.
  - *ICU bedside monitors:* HL7/MLLP, NEWS2, trends, anomalies and an LLM SBAR note.
  - *Imaging AI pre-read:* DICOM C-STORE, windowed viewer, and a vision-model pre-read scored against ground truth.
  - Any OpenAI-compatible provider works (Azure OpenAI, OpenAI, Hugging Face, DeepSeek); without one, a rule-based
    fallback is used.
- **CLI:** `iotcom hl7 listen|send|simulate`, `iotcom dicom listen|send|echo`.
- **Samples:** `Hl7MllpListener` console sample; notebook pair `medical/05-hl7-dicom`.
- **Docs:** HL7 and DICOM pages and the *Medical devices + AI* guide (EN/ID).

Not a medical device. All patients are fictional and all images are synthetic.

*Kesehatan: paket baru HL7 v2 (MLLP, ACK, simulator monitor pasien) dan adapter DICOM (C-STORE/C-ECHO, renderer,
studi sintetis); demo Galeri monitor ICU (NEWS2 + catatan SBAR dari LLM) dan pra-baca citra dengan model vision; perintah CLI
`hl7` / `dicom`; sampel, notebook, dan dokumentasi dua bahasa. Bukan perangkat medis.*

## 0.1.0-preview.1 — 2026-10-05

First preview (Phase 0 of the [roadmap](PLAN.md)).

- **Core:** endpoint model (client/server/publisher/subscriber), `ITransport` over System.IO.Pipelines, TCP and
  in-memory transports, traffic tap and frame anatomy, OpenTelemetry metrics/traces, reconnect policy, native loader.
- **Framing:** CRC catalogue (23 presets), LRC, SLIP, COBS, HDLC, line framing, streaming decoders.
- **Modbus:** TCP/RTU/ASCII master and slave, virtual PLC simulator, read-only mode, device identification;
  Rust engine (`IoTCom.Net.Native.Modbus`, native ABI 1).
- **NMEA 0183**, **Art-Net 4 / sACN**, **MQTT adapter + broker**, **SenML**, **serial transport**, **hosting**.
- **Tools:** `iotcom` CLI, Gallery desktop app, gateway web sample, console samples, templates, notebooks.
- **Docs:** 25 pages in English and Bahasa Indonesia.

*Rilis pratinjau pertama (Fase 0): inti, framing, Modbus (C# + mesin Rust), NMEA, Art-Net/sACN, MQTT, SenML, serial,
hosting, CLI, Galeri, sampel, template, notebook, dan dokumentasi dua bahasa.*
