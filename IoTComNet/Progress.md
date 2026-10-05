# IoTCom.Net — Progress

Development tracking for [PLAN.md](PLAN.md). Update this file whenever a component changes status.
Built by Gravicode Studios, led by Kang Fadhil.

**Current version:** `0.4.0-preview.1` · **Last update:** 2026-10-05

## Snapshot

| Area | Status | Evidence |
|---|---|---|
| .NET solution (`IoTCom.Net.slnx`) | ✅ builds clean, warnings as errors on libraries | `dotnet build IoTCom.Net.slnx` |
| .NET tests | ✅ 275 passing | `dotnet test tests/IoTCom.Net.Tests` |
| Rust workspace | ✅ 28 tests passing, clippy `-D warnings` clean; 5 cargo-fuzz targets (≈ 8.5 M local runs, no findings) | `cargo test --workspace`, `cargo clippy` |
| Cross-language conformance | ✅ CRC (115 vectors), COBS, SLIP, Modbus frames, CoAP messages (23) shared by C# and Rust; Rust engine ≡ managed framing | `conformance/`, `NativeModbusTests` |
| Docs EN/ID | ✅ 31 + 31 pages, parity and links verified | `python build/check_docs_parity.py` |
| Notebooks | ✅ 9 EN/ID pairs, code identical, all executed | `python build/check_notebooks.py` |
| Screenshots | ✅ Gallery (headless Skia), dashboard (Edge/CDP), CLI | `docs/images/` |
| NuGet packages | ✅ 14 packages (+12 symbol packages) published to nuget.org as `0.1.0-preview.1`; Native.Modbus carries 9 RIDs | release run on tag `iotcomnet-v0.1.0-preview.1` |

## Components

| Component | Status | Notes |
|---|---|---|
| IoTCom.Net.Abstractions | ✅ | endpoints, pub/sub, transport, traffic tap, frame anatomy, exceptions, product info |
| IoTCom.Net.Core | ✅ | TCP + in-memory transports, UDP + in-memory datagram network (loss/duplication), StreamTransport, EndpointBase, RecordingTap, HexDump, diagnostics, ReconnectPolicy, NativeLibraryLoader |
| IoTCom.Net.Framing | ✅ | 23 CRC presets (all pass check values), Crc16.Modbus fast path, LRC, SLIP, COBS, HDLC+FCS, LineFraming, pipe helpers |
| IoTCom.Net.Transport.Serial | ✅ | `UseSerial` / `ServeSerial`; hardware test pending (no RS-485 rig in CI) |
| IoTCom.Net.Protocols.Modbus | ✅ | TCP/RTU/ASCII, FC 1–6, 15, 16, 22, 23, 43/14; pipelining; read-only; simulator; anatomy |
| IoTCom.Net.Native.Modbus + Rust `iotcom-modbus` | ✅ win-x64 · ⏳ other RIDs | arm64/Linux/macOS binaries come from the CI native workflow |
| IoTCom.Net.Protocols.Nmea | ✅ | GGA/RMC/GSA/GSV/VTG/GLL/ZDA, GnssState, reader, server, simulator · AIS decode ⏳ |
| IoTCom.Net.Protocols.Dmx | ✅ | Art-Net ArtDmx/ArtPoll/ArtPollReply/ArtSync, sACN data + sequence rules, DmxUniverse · RDM ⏳ |
| IoTCom.Net.Adapters.Mqtt | ✅ | MQTTnet 5 adapter, reconnect + resubscribe, JSON/SenML helpers, embedded broker |
| IoTCom.Net.Serialization.SenML | ✅ | JSON + CBOR, resolution (RFC 8428 §4.6), builder |
| IoTCom.Net.Transport.Can | ✅ | ICanBus, SocketCAN (libc P/Invoke, CAN FD), slcan over serial/TCP (+ adapter emulator), virtual bus · PCAN/Kvaser/gs_usb ⏳ |
| IoTCom.Net.Protocols.IsoTp + Rust `iotcom-isotp` | ✅ | ISO 15765-2 classic + FD, fuzzed; native `iotcom_isotp` (ABI 1) |
| IoTCom.Net.Protocols.Uds | ✅ | UDS tester (read-only mode), OBD-II scan tool, ECU simulator with vehicle model · flashing helpers, DoIP, J1939 ⏳ |
| IoTCom.Net.Protocols.Coap + Rust `iotcom-coap` | ✅ | client/server, CON/NON reliability, dedup, separate responses, Observe, Block1/Block2, link-format, SenML, greenhouse simulator · DTLS/OSCORE/TCP ⏳ |
| IoTCom.Net.Protocols.Hl7 | ✅ | ER7 codec + escaping, MLLP server/client with ACK matching, LOINC vitals, PatientMonitorSimulator · ASTM E1394 ⏳ |
| IoTCom.Net.Adapters.Dicom | ✅ | fo-dicom 5.2.6 Storage SCP/SCU, C-ECHO, windowed renderer → PNG, synthetic CT/MR/X-ray with planted findings (not trimmable/AOT) |
| IoTCom.Samples.Medical (sample, not packed) | ✅ | NEWS2 (RCP 2017), trends, anomalies, OpenAI-compatible AI client, SBAR + imaging pre-read with ground-truth scoring |
| IoTCom.Net.Hosting | ✅ | AddIoTCom, IoTComEndpoints, keyed services, shared tap, health checks |
| IoTCom.Net (meta) | ✅ | protocol-specific hosting extensions |
| CLI `iotcom` (IoTCom.Net.Cli) | ✅ | Spectre.Console UI with the frame lane |
| Templates | ✅ | iotcom-console (modbus/nmea/mqtt × en/id), iotcom-worker |
| Gallery (Avalonia) | ✅ | 9 demos (Building: CoAP greenhouse; Automotive: vehicle diagnostics; Medical: ICU bedside monitors, imaging AI pre-read), Run/Code/Docs/Traffic, EN/ID runtime switch, light/dark, headless screenshot tool |
| IoTCom.Gateway web sample | ✅ | Modbus → MQTT (SenML), REST + SSE, HMI dashboard (EN/ID, light/dark, mobile) |
| Console samples | ✅ | ModbusMaster, ModbusSlaveSimulator, NmeaGpsReader, ArtNetPlayer, MqttSenMLBridge, Hl7MllpListener, UdsTester, CoapObserve |
| Benchmarks | ✅ | BenchmarkDotNet (short job, this machine): Modbus request round trip 5.0 µs sequential / 2.2 µs with 16 in flight over the in-memory transport (≈ 200k–450k req/s; design target ≥ 20k req/s on TCP loopback); CRC ≈ 2.2 ns/byte, zero allocations |
| CI | ✅ defined | repo root `.github/workflows/iotcomnet-ci.yml`, `iotcomnet-native.yml`, `iotcomnet-release.yml` (release on tag `iotcomnet-v*`, pushes with the `NUGET_API_KEY` secret) |
| VS Code extension | ⏳ not started | Phase 1 |

## Decisions taken during Phase 0

- **Modbus has two engines.** The managed `ModbusClient` is the default (works on every RID, AOT); the Rust engine
  (`NativeModbusClient`) is a drop-in `IModbusClient`. This answers open question 6 (managed fallback) for Modbus.
- **One DMX package.** Art-Net and sACN share the universe model and node plumbing, so they live in
  `IoTCom.Net.Protocols.Dmx` rather than two packages.
- **C# bindings are hand-written for ABI v1** (5 exported functions + 2 structs) and checked by the cross-language
  test; generated bindings (csbindgen) arrive with the next native crate.
- **Gallery screenshots are rendered headlessly** from the real window, so docs images stay reproducible in CI.

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
