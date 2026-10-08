# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Where this lives

IoTCom.Net is the `IoTComNet/` folder of the `Vibe_IoT` monorepo (other folders are unrelated projects). All commands
below run from `IoTComNet/`. GitHub workflows live at the **repo root** (`.github/workflows/iotcomnet-*.yml`) and are
scoped to this folder; NuGet releases run on tags `iotcomnet-vX.Y.Z` using the `NUGET_API_KEY` repository secret.
`solution-design.md` (Bahasa Indonesia) is the source of truth for scope and priorities; `PLAN.md` is the roadmap and
`Progress.md` must be updated whenever a component changes status.

## Commands

```bash
dotnet build IoTCom.Net.slnx                                           # everything (libraries build with warnings as errors)
dotnet test tests/IoTCom.Net.Tests                                     # all .NET tests
dotnet test tests/IoTCom.Net.Tests --filter "FullyQualifiedName~ModbusClientServerTests.Pipelined"   # single test
cd rust && cargo test --workspace                                      # Rust tests
cd rust && cargo clippy --workspace --all-targets -- -D warnings
cd rust && cargo build --release -p iotcom-modbus-native -p iotcom-isotp-native -p iotcom-ble-native -p iotcom-usb-native   # native libs (native tests skip without them)
dotnet pack IoTCom.Net.slnx -c Release -o artifacts/packages           # natives are picked up from artifacts/native/{rid}/
python conformance/generate.py        # regenerate shared C#/Rust test vectors
python build/generate_notebooks.py    # regenerate EN/ID notebooks from one spec (never edit .ipynb by hand)
python build/check_docs_parity.py     # EN/ID docs parity + broken links (CI gate)
python build/check_notebooks.py       # compile and run every notebook's code (CI gate)
dotnet run --project gallery/IoTCom.Net.Gallery.Screenshots -- docs/images   # re-render Gallery screenshots headlessly
node build/screenshot.mjs <url> <out.png> [w] [h] [waitMs] [dark]            # web screenshots (works with SSE pages)
dotnet run -c Release --project benchmarks/IoTCom.Net.Benchmarks -- --filter "*"
```

## Architecture

- **Curation tiers** (design §4): BCL/official features are never rebuilt; mature libraries are wrapped as adapters
  (MQTT → MQTTnet); only real gaps are implemented, in C# or in Rust. Rust is for hardware access and complex binary
  state machines; small stateless helpers (CRC, COBS, SLIP) stay in C# and are duplicated in Rust only when a crate
  needs them — kept in sync by `/conformance/*.json`, which both test suites execute.
- **Sans-I/O everywhere.** Codecs are pure (`ModbusPdu`, `ModbusFraming`, `ModbusAnatomy`, `NmeaParser`,
  `ArtNetPacket`, `SacnPacket`, `SenMLCodec`); endpoints only drive I/O. Rust protocols implement
  `iotcom_core::Machine`; the .NET driver (`NativeModbusClient`) feeds bytes, drains frames/events, and ticks timers.
- **Endpoint model** (`src/IoTCom.Net.Abstractions`): `IClientEndpoint`/`IServerEndpoint`/`IPublisher<T>`/`ISubscriber<T>`;
  concrete endpoints derive from `EndpointBase` (state machine, `Tap(...)` for the traffic tap, metrics). Transports are
  `ITransport` (an `IDuplexPipe`); builders implement `ITransportBuilder<T>` / `IListenerBuilder<T>` so the shared
  extensions `UseTcp`, `UseSerial` (Transport.Serial), `UseInMemory`/`ListenInMemory` work for every protocol.
  `InMemoryTransportListener` is how tests, notebooks and the Gallery run without hardware.
- **Modbus has two interchangeable engines** behind `IModbusClient`: managed `ModbusClient` and Rust `NativeModbusClient`
  (`src/IoTCom.Net.Native.Modbus` + `rust/crates`). The C ABI contract (ffi_guard, status codes, `iotcom_last_error`,
  `iotcom_abi_version` = `NativeMethods.ExpectedAbiVersion`) is in `rust/crates/iotcom-ffi-support`; bump both sides together.
  After changing any exported Rust signature run `cd rust && cargo run -p iotcom-bindgen` (headers in `rust/include`, C# reference in
  `tests/IoTCom.Net.Tests/Interop/Generated`) and fix what `BindingDriftTests` reports; CI fails when these files are stale.
- **Capture**: `PcapngTap` (Core) is an `ITrafficTap` writing Wireshark-native pcapng (CAN as SocketCAN, TCP/UDP protocols in
  synthetic IPv4 on their well-known ports — map new protocols in `PcapngTap.Encapsulation`). `iotcom sniff` relays and decodes.
- **Releases**: build, test and pack in **Release** before tagging (`dotnet build/test/pack -c Release`) — some analyzer rules only
  fire there, and CI also runs a NativeAOT publish that must keep analyzer projects out of AOT/RID settings.
- **Frame lane** (the visual signature): `FrameField`/`FrameFieldKind` describe frame fields; the CLI (`Ui.FrameLane`),
  the gateway dashboard and the Gallery (`FrameRow`, `FrameLaneView`) all render them with the same colours.
- **Datagrams & CoAP**: `IDatagramTransport` (Core: `UdpDatagramTransport`, `InMemoryDatagramNetwork` with `LossRate`/`DuplicateRate`)
  serves datagram protocols. `Protocols.Coap` is managed C# (`CoapStack` = message layer shared by `CoapClient`/`CoapServer`);
  Rust `iotcom-coap` is a fuzzed codec twin kept in sync by `/conformance/coap.json`.
- **MAVLink**: `Protocols.Mavlink` messages are generated at build time by `src/IoTCom.Net.Protocols.Mavlink.Generator` (Roslyn,
  netstandard2.0) from `Dialects/*.xml` (official, MIT). Never edit generated code; update the XML and run `python conformance/generate.py`
  (the Python reference checks CRC_EXTRA against published constants). The generator ships in the nupkg under `analyzers/dotnet/cs`.
- **Automotive**: `Transport.Can` (`ICanBus`, `CanBus.Create("socketcan:can0" | "slcan:COM5" | "slcan-tcp:h:p" | "virtual:x")`;
  `gsusb:` and `pcan:usbN` come from `Transport.Can.Adapters` via `CanBus.RegisterScheme`, call `CanAdapters.Register()` in hosts),
  `Protocols.CanOpen` (`CanOpenMaster`, `CanOpenNode`, `ObjectDictionary`, `CanOpenIoModuleSimulator`; Rust twin `iotcom-canopen`,
  `/conformance/canopen.json`), `Protocols.J1939` (`J1939Node` with address claim and BAM/RTS-CTS, `J1939Spn`, `J1939Dm1`,
  `J1939EngineSimulator`; Rust twin `iotcom-j1939`, `/conformance/j1939.json`),
  `Protocols.IsoTp` (Rust `iotcom-isotp` → native `iotcom_isotp`, driven by `IsoTpChannel`) and `Protocols.Uds` (`UdsClient`,
  `ObdClient`, `EcuSimulator`). Tests and CLI use `VirtualCanNetwork`; `--can sim` starts an in-process ECU. Fuzz targets live
  in `rust/fuzz` (nightly; `cargo +nightly fuzz run isotp`; on Windows put the MSVC `clang_rt.asan_dynamic` DLL on PATH).
- **LoRaWAN**: `Protocols.LoRaWan` is managed C# (`LoRaWanPacket` codec + `LoRaWanCrypto`, `SemtechPacket`/`SemtechPacketForwarder`,
  `LoRaWanNetworkServer`, sans-I/O `LoRaWanEndDevice`, `LoRaWanSimulator`); Rust `iotcom-lorawan` is the fuzzed twin kept in sync by
  `/conformance/lorawan.json` (Python reference with its own AES/CMAC). Tests use `InMemoryDatagramNetwork` and
  `HonorTimestamps = false` to skip the 5 s join delay; keys are secrets — never log them.
- **Metering**: `Protocols.Dlms` (`DlmsClient`/`DlmsServer` over `HdlcLink` or `WrapperLink`, codec `CosemData`/`HdlcFrame`/`DlmsApdu`,
  `DlmsSecurity` for suite 0, `DlmsMeterSimulator`) and `Protocols.MBus` (`MBusMaster`, `MBusSlaveSimulator`, `MBusTelegram`); Rust
  `iotcom-dlms`/`iotcom-mbus` are fuzzed codec twins (`/conformance/dlms.json`, `mbus.json`). Clients are read-only by default.
- **Field devices**: AIS is in `Protocols.Nmea` (`AisDecoder`, `AisTracker`, `AisBits`, `AisSimulator`); `Protocols.AtCommand`
  (`AtParser` sans-I/O, `AtModem`, `AtModemSimulator`); `Protocols.Astm` (`AstmMessage`, `AstmLink`, `AstmReceiver`/`AstmSender`,
  `AnalyzerSimulator`, `AstmToHl7`). AES-GCM with 12-byte tags must go through DLMS `Gcm12` (macOS only supports 16-byte tags).
- **Healthcare**: `Protocols.Hl7` (ER7 codec, MLLP endpoints, `PatientMonitorSimulator`) and `Adapters.Dicom` (fo-dicom
  wrapper, `DicomRenderer`, `SyntheticImaging`; not trimmable, not in the meta-package). Clinical analysis and the AI client
  live in `samples/shared/IoTCom.Samples.Medical` (sample code, never packed). AI config: `IOTCOM_AI_*` env vars or
  `%APPDATA%/IoTCom.Net/ai.json`; never log or commit keys. Everything medical is synthetic and labelled "not a medical device".
- **VS Code extension** (`tools/vscode-iotcom`, TypeScript): drives the hidden CLI command `iotcom rpc` (JSON-RPC 2.0 over
  stdio, `tools/iotcom-cli/Commands/RpcCommand.cs`) and never decodes protocols itself; add decoders/monitor sources in
  the CLI. `cd tools/vscode-iotcom && npm test` (needs a built CLI; picks the newest Debug/Release build),
  `npm run package` → `.vsix`, `node test/preview.mjs <dir>` renders the webviews for screenshots.
- **Plant networks**: `Protocols.Mdns` (DNS codec, `MdnsResponder`, `MdnsBrowser`, `MdnsSimulator` on the multicast-capable
  `InMemoryDatagramNetwork`) and `Protocols.Sparkplug` (payload via Google.Protobuf `CodedOutputStream`, no generated code;
  `SparkplugEdgeNode`/`SparkplugHost` over `MqttEndpoint`, whose `AbortAsync` simulates a lost link so the will fires).
  Payload formats implement `IPayloadCodec<T>` (Abstractions): `Serialization.Protobuf`/`.MessagePack` (optional, not in the
  meta-package), `.Tlv`, `SenMLPayloadCodec`.
- **Bluetooth LE**: `Transport.Ble` (`BleCentral`, `BlePeripheral`, codecs `BleUuid`/`AdvertisingData`/`GattValue`) over `IBleAdapter`:
  `NativeBleAdapter` (Rust `iotcom-ble-native` → `iotcom_ble`, btleplug; events are JSON lines drained by `iotcom_ble_poll_event`)
  or `VirtualBleNetwork` (tests, notebooks, Gallery, `--sim`). `cargo run -p iotcom-ble-native --example scan` checks the real radio.
- **USB/HID**: `Transport.Usb` (`UsbDevice`, `HidDevice`, `HidRelayBoard`, `UsbBulkTransport`/`UseUsbBulk`) over `IUsbBackend`:
  `NativeUsbBackend` (Rust `iotcom-usb-native` → `iotcom_usb`: nusb + hidapi, synchronous calls with timeouts) or `VirtualUsbBus`.
  The Gallery never opens real devices; `IOTCOM_GALLERY_SAMPLE_USB=1` (set by the screenshot tool) shows a fixed sample list.
- **OPC UA**: `Adapters.OpcUa` wraps the OPC Foundation stack (1.5.378, pinned; not in the meta-package, not AOT). PKI per
  application under `%LOCALAPPDATA%/IoTCom.Net/opcua/pki*`; tests and notebooks use temp PKI paths and `AcceptUntrustedCertificates`.
- **Hosting**: `AddIoTCom(...)` (Hosting) + protocol helpers `AddModbusClient/AddMqtt/...` (meta-package `src/IoTCom.Net`).

## Conventions specific to this repo

- Library settings come from `src/Directory.Build.props` (trimmable, AOT-compatible, XML docs, warnings as errors,
  curated `NoWarn`). Package versions are central in `Directory.Packages.props` (Avalonia pinned to 11.3.x).
- Every user-facing text is bilingual: docs (`docs/en` ↔ `docs/id`, front-matter `translation-status`), README pairs,
  Gallery strings (`Loc` / `Loc.L(en, id)` / `Text(en, id)`), dashboard `text.en/id` in `wwwroot/app.js`, template `--lang`.
- Credit line "Built by Gravicode Studios, led by Kang Fadhil" / "Dibuat oleh Gravicode Studios dipimpin oleh Kang
  Fadhil" is `IoTComInfo.CreditEn/CreditId`; keep it in apps and docs.
- Visual language (CLI, dashboard, Gallery, icon): RAL 7035 panel `#E4E5E0`, RAL 7016 anthracite `#2B3036`, IEC lamp
  colours amber `#F2A900` / blue / green `#2E9E5B` / red `#D23B2F`; Barlow Condensed + Barlow + JetBrains Mono.
  UI work should use the `frontend-design` skill.
- Gallery demos: logic in `Demos/XxxDemo.cs` (embedded and shown in the Code tab), view in `Demos/XxxDemo.View.cs`;
  update UI state only on the UI thread (`Ui(...)`).
- Device writes must respect read-only modes; the CLI requires `--allow-write` + confirmation.
- Avalonia gotchas seen here: `LetterSpacing` only on TextBlock; theme-dictionary brushes from code need
  `GetResourceObservable`; tab headers are set in code-behind on language change.
