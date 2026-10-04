# IoTCom.Net — Progress

Development tracking for [PLAN.md](PLAN.md). Update this file whenever a component changes status.
Built by Gravicode Studios, led by Kang Fadhil.

**Current version:** `0.1.0-preview.1` · **Last update:** 2026-10-05

## Snapshot

| Area | Status | Evidence |
|---|---|---|
| .NET solution (`IoTCom.Net.slnx`) | ✅ builds clean, warnings as errors on libraries | `dotnet build IoTCom.Net.slnx` |
| .NET tests | ✅ 192 passing | `dotnet test tests/IoTCom.Net.Tests` |
| Rust workspace | ✅ 16 tests passing, clippy `-D warnings` clean | `cargo test --workspace`, `cargo clippy` |
| Cross-language conformance | ✅ CRC (115 vectors), COBS, SLIP, Modbus frames shared by C# and Rust; Rust engine ≡ managed framing | `conformance/`, `NativeModbusTests` |
| Docs EN/ID | ✅ 25 + 25 pages, parity and links verified | `python build/check_docs_parity.py` |
| Notebooks | ✅ 6 EN/ID pairs, code identical, all executed | `python build/check_notebooks.py` |
| Screenshots | ✅ Gallery (headless Skia), dashboard (Edge/CDP), CLI | `docs/images/` |
| NuGet packages | ✅ 15 packages packed; templates and CLI verified end-to-end from the local feed · ⏳ not yet published | `dotnet pack IoTCom.Net.slnx -c Release -o artifacts/packages` |

## Components

| Component | Status | Notes |
|---|---|---|
| IoTCom.Net.Abstractions | ✅ | endpoints, pub/sub, transport, traffic tap, frame anatomy, exceptions, product info |
| IoTCom.Net.Core | ✅ | TCP + in-memory transports, StreamTransport, EndpointBase, RecordingTap, HexDump, diagnostics, ReconnectPolicy, NativeLibraryLoader |
| IoTCom.Net.Framing | ✅ | 23 CRC presets (all pass check values), Crc16.Modbus fast path, LRC, SLIP, COBS, HDLC+FCS, LineFraming, pipe helpers |
| IoTCom.Net.Transport.Serial | ✅ | `UseSerial` / `ServeSerial`; hardware test pending (no RS-485 rig in CI) |
| IoTCom.Net.Protocols.Modbus | ✅ | TCP/RTU/ASCII, FC 1–6, 15, 16, 22, 23, 43/14; pipelining; read-only; simulator; anatomy |
| IoTCom.Net.Native.Modbus + Rust `iotcom-modbus` | ✅ win-x64 · ⏳ other RIDs | arm64/Linux/macOS binaries come from the CI native workflow |
| IoTCom.Net.Protocols.Nmea | ✅ | GGA/RMC/GSA/GSV/VTG/GLL/ZDA, GnssState, reader, server, simulator · AIS decode ⏳ |
| IoTCom.Net.Protocols.Dmx | ✅ | Art-Net ArtDmx/ArtPoll/ArtPollReply/ArtSync, sACN data + sequence rules, DmxUniverse · RDM ⏳ |
| IoTCom.Net.Adapters.Mqtt | ✅ | MQTTnet 5 adapter, reconnect + resubscribe, JSON/SenML helpers, embedded broker |
| IoTCom.Net.Serialization.SenML | ✅ | JSON + CBOR, resolution (RFC 8428 §4.6), builder |
| IoTCom.Net.Hosting | ✅ | AddIoTCom, IoTComEndpoints, keyed services, shared tap, health checks |
| IoTCom.Net (meta) | ✅ | protocol-specific hosting extensions |
| CLI `iotcom` (IoTCom.Net.Cli) | ✅ | Spectre.Console UI with the frame lane |
| Templates | ✅ | iotcom-console (modbus/nmea/mqtt × en/id), iotcom-worker |
| Gallery (Avalonia) | ✅ | 5 demos, Run/Code/Docs/Traffic, EN/ID runtime switch, light/dark, headless screenshot tool |
| IoTCom.Gateway web sample | ✅ | Modbus → MQTT (SenML), REST + SSE, HMI dashboard (EN/ID, light/dark, mobile) |
| Console samples | ✅ | ModbusMaster, ModbusSlaveSimulator, NmeaGpsReader, ArtNetPlayer, MqttSenMLBridge |
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

## Known gaps

- Native binaries for Linux, macOS and Windows ARM64 are not built on this machine (the MSVC ARM64 tools and cross
  toolchains are not installed). The workflow `iotcomnet-native.yml` builds them.
- No `cargo-fuzz` targets yet (a randomised decoder test runs in `cargo test`).
- Serial and Art-Net broadcast paths are verified on loopback/in-memory only; no hardware-in-the-loop rig yet.

## Log

| Date | Change |
|---|---|
| 2026-10-05 | First CI run: fixed a start/stop race in DMX, NMEA and Modbus server loops (token read lazily from a field; caught on Alpine) and musl `cdylib` output (`-crt-static`). |
| 2026-10-05 | Moved into the `Vibe_IoT` monorepo (`IoTComNet/`); workflows scoped to this folder. NativeAOT: trim/AOT analysis of a published sample is warning-free (local native link needs the VS developer environment; CI covers it on Linux). |
| 2026-10-05 | Phase 0 delivered as `0.1.0-preview.1`: core libraries, Modbus (C# + Rust), NMEA, Art-Net/sACN, MQTT, SenML, hosting, CLI, Gallery, gateway sample, console samples, templates, notebooks, EN/ID docs, CI definitions, NuGet icon. |
| 2026-10-04 | Solution design approved (solution-design.md). |
