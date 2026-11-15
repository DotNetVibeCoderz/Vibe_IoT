# Changelog

All notable changes to IoTCom.Net. Versions follow SemVer; the native ABI version is tracked separately
(`iotcom_abi_version()`). *Bahasa Indonesia di bawah setiap rilis.*

## Unreleased

- Matter: DER integers (certificate serials, signature r/s) are written in minimal form, so a value with leading zero bytes no longer
  throws (`ToX509Der` failed for about 1 in 128 signatures; `TbsDer` for about 1 in 65 536 serials).

*Matter: integer DER kini selalu dalam bentuk minimal.*

## 0.30.0-preview.1 — 2026-11-15

Matter: controller, device and bridge. This completes the protocol list of Phase 2.

- **New package `IoTCom.Net.Protocols.Matter`** (separate from the meta-package, like DTLS: it uses BouncyCastle for P-256 and AES-CCM):
  TLV, onboarding payloads (QR and manual codes), message and protocol headers, Interaction Model messages, operational certificates with
  the exact X.509 DER they sign; SPAKE2+ (PASE), CASE, MRP, message counters and session keys; `MatterController` (commissioning with
  fail-safe, attestation, CSR, root and NOC, CASE and step progress; read with wildcards, write, invoke, subscribe; **read-only by default**;
  `FabricFile` persistence), `MatterDevice` (root node clusters, fabric table, PASE/CASE responders, Interaction Model server with
  subscriptions, fail-safe roll-back, admin-only access), `MatterBridge` and `MatterBridgeSimulator`, `MatterDiscovery` (DNS-SD over
  IoTCom's mDNS), `MatterAttestation` with a published test PAA, `AddMatterController`/`AddMatterDevice`.
- **New Rust crate `iotcom-matter`**: fuzzed twin of TLV, onboarding payloads and message headers (17th fuzz target `matter`, about 4 M local
  runs, no findings); `/conformance/matter.json` (41 vectors) from an independent Python reference that reproduces the CHIP SDK's published
  onboarding example and the spec's compressed-fabric-id example and computes two complete SPAKE2+ exchanges.
- Interoperability: verified against those references and between IoTCom's controller and device across processes over UDP; not yet against
  other Matter stacks (see the docs page).
- CLI `iotcom matter code|device|discover|commission|read|onoff|subscribe|demo|decode`, VS Code decoders `matter` and `matter-tlv`, pcapng on
  UDP 5540, Gallery *Smart home over Matter*, MatterHome sample, notebook pair `building/31-matter`, docs page *Matter*, benchmark
  (`MatterBenchmarks`).

*Matter: controller, perangkat, dan bridge dengan PASE/CASE, atestasi, sertifikat operasional, Interaction Model dengan subscription, dan
DNS-SD; kembaran codec Rust yang di-fuzz dan referensi Python independen; CLI, Gallery, sampel, notebook, dan dokumentasi. Daftar protokol
Fase 2 selesai.*

## 0.29.0-preview.1 — 2026-11-15

Probe and flash microcontrollers: SWD/JTAG, RTT and USB DFU.

- **New package `IoTCom.Net.Debug`** (in the meta-package): `DebugProbe` (attach by probe-rs target name, memory reads, core state, and —
  **after `AllowWrites()`** — flashing with progress, erase all, memory writes, halt/run/reset) over `IProbeBackend`: `NativeProbeBackend`
  (new native library `iotcom_probe` on probe-rs 0.32: CMSIS-DAP, ST-Link, J-Link, ESP USB-JTAG, FTDI, WCH-Link) or `VirtualProbeBackend`;
  `RttClient` (SEGGER RTT over any `ITargetMemory`); `DfuClient` (USB DFU 1.1 + ST DfuSe over Transport.Usb) with `DfuSeLayout`;
  `FirmwareImage`, `IntelHex`, `DfuFile`, `Elf`, `Crc32`; simulators `VirtualMcu` (demo firmware with RTT log and commands) and
  `VirtualDfuDevice` (STM32 system bootloader). `AddDebugProbe` for hosting.
- **New Rust crates**: `iotcom-probe-native` (C ABI over probe-rs, bindings checked by the drift tests, built for every RID best effort) and
  `iotcom-firmware` (fuzzed twin of the firmware/RTT codecs, 16th fuzz target `firmware`, 3 M local runs; it found and fixed a UTF-8 slicing
  panic) with `/conformance/firmware.json` (27 vectors from the Python reference).
- Licensing: probe-rs requires `serialport` (MPL-2.0, file-level copyleft); it ships unmodified inside `iotcom_probe`, is named in NOTICE and
  is the only MPL exception for shipped code in `deny.toml`. `bincode` (unmaintained, RUSTSEC-2025-0141) is ignored with a reason.
- CLI `iotcom probe list|info|read|flash|rtt|control|image` and `iotcom dfu list|info|upload|download`, VS Code decoders `ihex`, `dfu-suffix`,
  `rtt`, Gallery *Probe and flash firmware* (design use case 12 — the Gallery now covers all 17), ProbeFlash sample, notebook pair
  `devices/30-probe-flash`, docs page *SWD/JTAG, RTT and DFU*, benchmark (`FirmwareBenchmarks`).

*Probe dan flash mikrokontroler: probe SWD/JTAG lewat probe-rs, log RTT, pembaruan USB DFU/DfuSe, dan format firmware (hanya-baca secara
bawaan), dengan MCU, probe, dan bootloader simulasi serta kembaran codec Rust yang di-fuzz; Gallery kini mencakup ke-17 use case.*

## 0.28.0-preview.1 — 2026-11-15

EtherCAT master.

- **New package `IoTCom.Net.Protocols.EtherCat`** (in the meta-package): `EcatFrame`/`EcatDatagram`, `EcatMailbox`, `CoeSdo`, `EcatSii` and
  `EcatAlStatus` codecs; `EthercatMaster` (line scan with station addresses and SII identity, AL state machine with mailbox and process-data
  SyncManagers and FMMUs, cyclic LRW exchange with working-counter statistics, CoE SDO upload/download, error acknowledgement, **read-only by
  default**: SAFE-OP and a logical read until `AllowWrites()`); `RawEthernetLink` (AF_PACKET on Linux, Npcap/libpcap elsewhere);
  `VirtualEthercatSegment` with simulated ESCs (cable cut, frame loss) and `EthercatRackSimulator` (conveyor I/O rack). `AddEthercatMaster`
  brings the line back to PRE-OP when the host stops.
- **New Rust crate `iotcom-ethercat`**: fuzzed codec twin (15th fuzz target `ethercat`, 3 M local runs, no findings); `/conformance/ethercat.json`
  with 37 vectors from the Python reference, run by both suites.
- CLI `iotcom ethercat interfaces|scan|watch|sdo|set|demo|decode`, VS Code decoder `ethercat`, pcapng as LINKTYPE_ETHERNET (Wireshark's
  EtherCAT dissector, checked by the tshark gate), Gallery *I/O rack over EtherCAT*, EthercatRack sample, notebook pair `industrial/29-ethercat`,
  docs page *EtherCAT*, benchmark (`EthercatBenchmarks`).
- Release workflow: the app publish step is fixed and the nuget.org push runs in its own job, so the GitHub release (with packages and portable
  apps) no longer waits on the NuGet key.

*Master EtherCAT: pemindaian jalur, state machine AL, process image dengan LRW siklik, objek CoE (hanya-baca secara bawaan), Ethernet mentah
dan rak I/O simulasi, kembaran codec Rust yang di-fuzz; CLI, Gallery, sampel, notebook, dan dokumentasi.*

## 0.27.0-preview.1 — 2026-11-15

DTLS 1.2: CoAP and LwM2M over `coaps://`.

- **New package `IoTCom.Net.Transport.Dtls`** (adapter over BouncyCastle 2.7.0, MIT; trimmable and NativeAOT-clean, not in the
  meta-package): `DtlsClientTransport` and `DtlsServerTransport` as `IDatagramTransport`s, PSK (`TLS_PSK_WITH_AES_128_CCM_8`, GCM) and
  ECDHE_ECDSA certificates (pinned SHA-256 fingerprints or custom roots), HelloVerifyRequest cookies, per-peer sessions with idle expiry,
  `SessionEstablished`/`HandshakeFailed`, `UseDtlsClient`/`UseDtlsServer` for every datagram builder, `DtlsCertificates.CreateSelfSigned`.
  A failed handshake stays failed and keeps reporting its real cause.
- CLI: every `iotcom coap` command accepts `coaps://` with `--psk identity:hex` (or `IOTCOM_DTLS_PSK`) or `--pin`; `iotcom coap serve --psk|--cert`.
- CoapsSecure sample (also in the CI NativeAOT gate), notebook pair `network/28-dtls`, docs page *DTLS*, benchmark (`DtlsBenchmarks`).
- Gallery: *Cellular module over AT commands* (design use case 14) and *Pub/sub across brokers* (use case 17: MQTT, AMQP 1.0 and Zenoh
  side by side, with a bridge mode).
- `AtModemSimulator.Rssi` and `ChangeRegistration` let tests and demos drive coverage (tunnels, roaming); the simulator accepts `AT+CNMI`.
- Release pipeline: optional Authenticode, NuGet author signing, Developer ID signing and notarisation; portable CLI and Gallery
  archives attached to each GitHub release; docs page *Releasing and signing*.

*DTLS 1.2 untuk CoAP dan LwM2M (coaps://): transport klien dan server dengan PSK dan sertifikat ECDSA ber-pin, cookie, sesi per peer;
CLI, sampel, notebook, dan dokumentasi. Juga: Gallery modul seluler lewat perintah AT dan pub/sub lintas broker; simulator modem dapat
mengatur cakupan; pipeline rilis dengan penandatanganan opsional dan arsip aplikasi portabel.*

## 0.26.0-preview.1 — 2026-11-15

EtherNet/IP explicit messaging.

- **New package `IoTCom.Net.Protocols.EtherNetIp`** (in the meta-package): `EnipPacket` (encapsulation, CPF, SendRRData), `CipPath`
  (logical and symbolic EPATHs), `CipRequest`/`CipResponse`, `EnipIdentity`, `CipTagValue`; `EnipClient` (sessions, Get/Set_Attribute,
  Get_Attributes_All, Logix Read/Write Tag, Multiple_Service_Packet, Unconnected_Send routing, **read-only by default**), `EnipDiscovery`
  (ListIdentity over UDP), `EnipAdapter` (Identity, Assembly, symbolic tags) and `EnipPlcSimulator` (packaging line).
- **New Rust crate `iotcom-enip`**: fuzzed codec twin (14th fuzz target `enip`, 300k local runs, no findings); `/conformance/enip.json`
  with 28 vectors from the Python reference, run by both suites.
- CLI `iotcom enip discover|identity|read|write|attr|serve|demo|decode`, VS Code decoder `enip`, pcapng on TCP 44818, Gallery *Packaging line over
  EtherNet/IP*, EnipTags sample, notebook pair `industrial/27-ethernetip`, docs page *EtherNet/IP*, `AddEnipClient`/`AddEnipAdapter`, benchmark.

*EtherNet/IP explicit messaging: klien (hanya-baca secara bawaan), penemuan ListIdentity, adapter dengan tag Logix, simulator PLC, dan
kembaran codec Rust yang di-fuzz; CLI, Gallery, sampel, notebook, dan dokumentasi.*

## 0.25.0-preview.1 — 2026-11-15

BACnet/IP: our own server API and a client adapter.

- **New package `IoTCom.Net.Protocols.Bacnet`** (in the meta-package): `BacnetWriter`/`BacnetReader` tag codecs, `BacnetPacket`
  (BVLC, NPDU, every APDU type), `BacnetServices`, `BacnetDevice` (Who-Is/Who-Has, ReadProperty, ReadPropertyMultiple with
  ALL/REQUIRED/OPTIONAL, WriteProperty with priorities, SubscribeCOV with confirmed or unconfirmed notifications), `BacnetObject`
  (analog/binary/multi-state with priority arrays and COV increments) and `BacnetAhuSimulator`.
- **New package `IoTCom.Net.Adapters.Bacnet`** (not in the meta-package; not trimmable/AOT): `BacnetIpClient` over ela-compil's
  BACnet library 4.0.0 (MIT) with discovery, reads, guarded writes and COV, bridged onto IoTCom datagram transports so it runs in
  memory too. Interop tests run it against our own device.
- `/conformance/bacnet.json`: 45 vectors from the Python reference; pcapng maps BACnet to UDP 47808; `UdpDatagramTransport.EnableBroadcast`.
- CLI `iotcom bacnet discover|objects|read|write|subscribe|serve|demo|decode`, VS Code decoder `bacnet`, Gallery *Air handling unit over
  BACnet*, BacnetBms sample, notebook pair `building/26-bacnet`, docs page *BACnet*, `AddBacnetDevice`/`AddBacnetClient`, benchmark.
- CI: the Wireshark interop gate feeds tshark from stdin (newer packages refused to open the capture by path).

*BACnet/IP: API server sendiri (device dengan priority array dan COV) dan adapter klien di atas pustaka BACnet ela-compil, diuji
saling berbicara; simulator AHU, CLI, Gallery, sampel, notebook, dan dokumentasi.*

## 0.24.0-preview.1 — 2026-11-15

KNXnet/IP for building automation.

- **New package `IoTCom.Net.Protocols.Knx`** (in the meta-package): `KnxIndividualAddress`/`KnxGroupAddress`, `KnxCemiFrame`
  (cEMI L_Data), `KnxIp` (KNXnet/IP header, HPAI, CRI/CRD, DIBs and every core, tunnelling and routing service), `KnxDpt`
  (DPT 1–3, 5–14, 16–18, 20.102, 232.600 with names and units), `KnxTunnelClient` (heartbeats, sequence counters, ack retry,
  L_Data.con, **read-only by default**), `KnxRouter` (multicast routing), `KnxDiscovery`, `KnxIpGateway` (a KNX IP interface
  and router in software), `KnxBus` and `KnxBuildingSimulator` (an office floor with lights, blinds, heating and presence).
- `/conformance/knx.json`: 56 vectors (DPT encodings with the KNX float's edges, cEMI and KNXnet/IP frames, malformed input)
  from the Python reference; pcapng maps KNXnet/IP to UDP 3671 for Wireshark.
- CLI `iotcom knx discover|monitor|read|write|demo|decode` (`--sim`, writes need `--allow-write`), VS Code decoders `knxip` and
  `cemi`, Gallery *Office floor over KNX* (Smart building, design use case 6), KnxMonitor sample, notebook pair
  `building/25-knx`, docs page *KNX*, `AddKnxTunnel`/`AddKnxRouter` hosting helpers, KNX benchmark.

*KNXnet/IP untuk otomasi gedung: klien tunnelling (hanya-baca secara bawaan), routing, penemuan, router KNX IP dalam perangkat
lunak, cEMI, datapoint type, dan simulator lantai kantor; CLI, Gallery lantai kantor dengan group monitor, sampel, notebook,
dan dokumentasi.*

## 0.23.0-preview.1 — 2026-11-15

OCPP 1.6J and 2.0.1 for EV charging.

- **New package `IoTCom.Net.Protocols.Ocpp`** (in the meta-package): `OcppFrame` (sans-I/O OCPP-J codec with the error
  code each version answers with, frame lane), `OcppSession` (RPC framework over WebSocket: one outstanding CALL per side,
  timeouts, typed and raw handlers, automatic NotImplemented/format errors), typed messages for the core, smart charging and
  remote trigger profiles in `V16` and `V201` with source-generated JSON (trimmable, NativeAOT), `OcppCatalog`,
  `OcppChargePoint` (ws/wss via `ClientWebSocket` with Basic auth and client certificates, or any IoTCom transport),
  `OcppCsms` (own WebSocket upgrade over TCP or in-memory with optional TLS and password check, `AcceptWebSocketAsync` for
  ASP.NET Core, live station model, version-independent commands, **read-only by default**) and
  `OcppChargePointSimulator` (EV model with taper and power limits, meter values, heartbeats, reconnects, every command).
- `/conformance/ocpp.json`: 35 vectors (specification examples, 13 malformed frames with the per-version error code, 16
  schema-valid payloads) from the Python reference.
- CLI `iotcom ocpp csms|charger|demo|decode` (commands need `--allow-write`), VS Code decoder `ocpp`, Gallery *EV charging
  hub over OCPP* (Energy & grid, design use case 11) with load management, OcppCharger sample, notebook pair
  `automotive/24-ocpp`, docs page *OCPP*, `AddOcppCsms`/`AddOcppChargePoint` hosting helpers, OCPP benchmarks.
- Security: the .NET servicing packages move from 10.0.12 to 10.0.15 (System.Formats.Cbor CVE-2026-118747,
  GHSA-vmv3-wvjp-wqpj).
- Docs: the Zenoh notebook no longer says the native library ships.

*OCPP 1.6J dan 2.0.1 untuk pengisian EV: codec RPC, sesi WebSocket, pesan bertipe, charge point, CSMS kecil (hanya-baca
secara bawaan), dan simulator pengisi dengan model EV; CLI, Gallery hub pengisian dengan manajemen beban, sampel, notebook,
dan dokumentasi. Paket servicing .NET naik ke 10.0.15 karena advisori keamanan System.Formats.Cbor.*

## 0.22.0-preview.1 — 2026-10-10

Zenoh, NATS, AMQP 1.0 and Kafka.

- **New package `IoTCom.Net.Adapters.Zenoh`** (not in the meta-package) over the Rust `zenoh` crate 1.10.1 (TCP and UDP
  transports only; native library `iotcom_zenoh` from `rust/crates/native/iotcom-zenoh-native`, **not shipped in this release**:
  zenoh 1.10.1 compiles in `lz4_flex` 0.10 with RUSTSEC-2026-0041 and no compatible fix, so the crate stays out of the workspace): `ZenohSession`
  (put, delete, `Subscribe`, `SampleReceived`, `WatchAsync`, queryables, `GetAsync`, peer or client mode, `ReadOnly` option,
  traffic tap, `IPublisher`/`ISubscriber`), `ZenohKeyExpr` (pure C# `*`, `**`, `$*` matching), `VirtualZenohNetwork` (in-process,
  no native library needed) and `NativeZenohBackend`.
- **New packages `IoTCom.Net.Adapters.Nats`** (NATS.Client.Core 3.3.0: wildcards, queue groups, headers, request/reply),
  **`IoTCom.Net.Adapters.Amqp`** (AMQPNetLite.Core 2.5.4: confirmed and pre-settled sends, redelivery, `AmqpMiniBroker` in-process
  broker for tests and demos) and **`IoTCom.Net.Adapters.Kafka`** (Confluent.Kafka 2.16.0: keys, headers, QoS mapped to acks,
  consumer groups, regular-expression topics, TLS/SASL; not trimmable or AOT-compatible). Hosting helpers `AddNats`,
  `AddAmqp`, `AddKafka` live in each package. None is in the meta-package.
- CLI `iotcom zenoh sub|get|pub` (`pub` needs `--allow-write`; `--sim` runs an in-process plant). CI job `brokers` runs the NATS and
  Kafka integration tests against service containers (`IOTCOM_NATS_URL`, `IOTCOM_KAFKA_BOOTSTRAP`; they skip without them).
- Notebook pairs `messaging/22-zenoh` and `messaging/23-brokers`; docs pages *Zenoh* and *Messaging brokers*.
- Fixes: `MdnsBrowser.BrowseAsync` TimeSpan overflow; sturdier NTP multi-server delay filter (`SntpClient.SynchronizeAsync`);
  timing-dependent Sparkplug, NTP and LoRaWAN tests made deterministic.

*Adapter Zenoh (di atas crate Rust, dengan jaringan virtual), serta NATS, AMQP 1.0 (dengan mini broker dalam proses), dan Kafka,
dengan CLI `zenoh`, notebook, dan dokumentasi; perbaikan overflow TimeSpan di mDNS dan filter server NTP yang lebih kokoh.*

## 0.21.0-preview.1 — 2026-10-10

OMA LwM2M.

- **New package `IoTCom.Net.Protocols.Lwm2m`** (also in the meta-package), C# on IoTCom.Net's CoAP: `Lwm2mClient`
  (registration, updates at 80 % of the lifetime with re-registration, deregistration; Read, Discover, Write, Execute,
  Observe with pmin/pmax, observation cancel; `Lwm2mInstance` with write validators and execute handlers; `AbortAsync`),
  `Lwm2mServer` (registrations with expiry and events; Read, Discover, Write, Execute, Write-Attributes, Observe;
  read-only until `AllowWrites()`), `Lwm2mContent` (TLV, plain text, opaque, SenML JSON and CBOR, TLV frame lane),
  `Lwm2mRegistry` with Server, Device, Location, Temperature and Light Control, and `Lwm2mStreetLightSimulator`.
- 19 shared TLV vectors in `/conformance/lwm2m.json` from an independent Python reference.
- CLI `iotcom lwm2m serve|client|demo|decode`; RPC decoder `lwm2m-tlv`; hosting `AddLwm2mServer`/`AddLwm2mClient`; Gallery
  *Street lights over LwM2M*; sample `Lwm2mClient`; notebook pair `messaging/21-lwm2m`; docs page *LwM2M*.
- Fixes: three timing-dependent tests (Sparkplug late host, NTP server selection, LoRaWAN downlink) and a sturdier NTP
  multi-server filter.

*OMA LwM2M (klien dan server di atas CoAP, TLV/SenML, observe, server hanya-baca secara bawaan) dengan CLI, demo Galeri,
sampel, notebook, dan dokumentasi.*

## 0.20.0-preview.1 — 2026-10-09

NFC / NDEF.

- **New package `IoTCom.Net.Protocols.Nfc`** (also in the meta-package): NDEF codec (Text in UTF-8/UTF-16, URI prefix
  codes, Smart Poster, MIME, absolute URI, external types, Android Application Records, Wi-Fi credentials with masked
  keys, long and chunked records, frame lane); NFC Forum Type 2 tag memory (NTAG213/215/216: UID check bytes,
  capability container, TLVs with 3-byte lengths, page map); `Type2TagClient` over PC/SC storage-card APDUs with writes
  off by default, only changed pages written, tear-safe ordering and pages 0–3 protected; `PcscNfcReader` on Windows,
  Linux and macOS preferring contactless readers; `VirtualNfcReader` and `VirtualType2Tag`.
- 30 shared vectors in `/conformance/ndef.json` from an independent Python reference (C# only, as designed).
- CLI `iotcom nfc readers|read|write|decode`; RPC decoder `ndef`; Gallery *NFC asset tags*; sample `NfcTagReader`;
  notebook pair `devices/20-nfc`; docs page *NFC / NDEF*.
- Fix: the Sparkplug late-host rebirth test waits for the first NBIRTH (flaky on the Alpine runner).

*NFC/NDEF (codec NDEF, memori tag Type 2, pembaca PC/SC di tiga OS, penulisan yang dijaga) dengan CLI, demo Galeri,
sampel, notebook, dan dokumentasi.*

## 0.19.0-preview.1 — 2026-10-09

NTP / SNTP.

- **New package `IoTCom.Net.Protocols.Ntp`** (also in the meta-package): `SntpClient` (RFC 4330 answer checks with a
  random-nonce originate timestamp, kiss-o'-death, leap alarm, multi-server median estimate, 15 s minimum poll interval,
  replaceable clock; never sets the system clock), `NtpServer` (stratum, reference, leap indicator, kiss-o'-death RATE
  rate limiting), `NtpPacket` and era-aware `NtpTimestamp` (1968–2104 across 2036), `NtpMath`, the frame lane, and
  `DriftingClock` for simulations.
- **New Rust crate `iotcom-ntp`**: the codec twin, fuzz target `ntp` (6.7 M local runs, no findings) and 25 shared
  vectors in `/conformance/ntp.json` from an independent Python reference.
- `InMemoryDatagramNetwork` gains `Latency` and `Jitter` (default zero).
- CLI `iotcom ntp query|serve`; RPC decoder `ntp`; pcapng on UDP 123; hosting `AddSntpClient`/`AddNtpServer`; Gallery
  *Fleet clock sync over NTP*; sample `NtpClock`; notebook pair `network/19-ntp`; docs page *NTP / SNTP*.
- Fix: the IEC 104 codecs reject CP56Time2a years above 99, which did not survive a round trip (found by fuzzing).

*NTP/SNTP (klien dengan pemeriksaan RFC 4330, server dengan pembatasan laju, cap waktu sadar era) dengan kembaran Rust
yang di-fuzz, CLI, demo Galeri, sampel, notebook, dan dokumentasi; perbaikan tahun CP56Time2a di IEC 104.*

## 0.18.0-preview.1 — 2026-10-09

IEC 60870-5-104.

- **New package `IoTCom.Net.Protocols.Iec104`** (also in the meta-package): `Iec104Client` (STARTDT/STOPDT, point table,
  general/group/counter interrogation, read, single/double/step commands and float/scaled/normalized set points, direct
  or select-before-operate, clock sync; read-only until `AllowCommands()`), `Iec104Server` (point table, spontaneous
  time-tagged changes, command handlers, mandatory SBO option, negative answers for unknown type/cause/address, end of
  initialisation), the APCI link (sequence checks, k/w windows, t1/t2/t3, TESTFR), the codec with CP56Time2a and the
  frame lane, and a 20 kV feeder bay simulator with interlocks, protection trip, tap changer and energy counter.
- **New Rust crate `iotcom-iec104`**: the codec twin, fuzz target `iec104` (1.6 M local runs, no findings) and 53 shared
  vectors in `/conformance/iec104.json` from an independent Python reference.
- CLI `iotcom iec104 gi|read|monitor|command|serve`; RPC decoder `iec104`; pcapng on TCP 2404; hosting
  `AddIec104Client`/`AddIec104Server`; Gallery *Substation control over IEC 104* in a new *Energy & grid* category; sample
  `Iec104Scada`; notebook pair `industrial/18-iec104`; docs page *IEC 60870-5-104*.

*IEC 60870-5-104 (master SCADA dan RTU, interogasi, data spontan bertanda waktu, select-before-operate) dengan kembaran
Rust yang di-fuzz, CLI, demo Galeri, sampel, notebook, dan dokumentasi.*

## 0.17.0-preview.1 — 2026-10-09

SAE J1939.

- **New package `IoTCom.Net.Protocols.J1939`** (also in the meta-package): `J1939Node` (address claim with NAME
  arbitration, BAM and RTS/CTS transport protocol up to 1785 bytes, requests and responders, listen-only and read-only
  modes, `Claims`), `J1939Id`, `J1939Name`, `J1939Spn` (EEC1, EEC2, CCVS1, ET1, EFL/P1, LFE1, VEP1, HOURS with the
  frame lane), `J1939Dm1`/`J1939Dtc`, and `J1939EngineSimulator` with an oil-leak fault.
- **New Rust crate `iotcom-j1939`**: the codec twin, fuzz target `j1939` (2.9 M local runs, no findings) and 28 shared
  vectors in `/conformance/j1939.json` from an independent Python reference.
- CLI `iotcom j1939 monitor|request|claims`; RPC decoder `j1939`; Gallery *Truck cluster over J1939*; sample
  `J1939Monitor`; notebook pair `automotive/17-j1939`; docs page *SAE J1939*.

*SAE J1939 (klaim alamat, transport protocol BAM dan RTS/CTS, SPN, DM1) dengan kembaran Rust yang di-fuzz, CLI, demo
Galeri, sampel, notebook, dan dokumentasi.*

## 0.16.0-preview.1 — 2026-10-09

CANopen.

- **New package `IoTCom.Net.Protocols.CanOpen`** (also in the meta-package): `CanOpenMaster` (NMT, heartbeat consumer,
  SDO client expedited/segmented with abort codes, SYNC, PDO and EMCY events, TPDO mapping, network scan, read-only
  mode), `CanOpenNode` (boot-up, NMT state machine, heartbeat producer, SDO server, event/SYNC TPDOs, RPDOs,
  emergencies), `ObjectDictionary`/`PdoMapping`, `CanOpenCodec` with the frame lane, and a CiA 401-style I/O module
  simulator.
- **New Rust crate `iotcom-canopen`**: the codec twin, fuzz target `canopen` (7 M local runs, no findings) and 71
  shared vectors in `/conformance/canopen.json` from an independent Python reference.
- CLI `iotcom canopen scan|read|write|nmt|monitor`; RPC decoder `canopen`; hosting `AddCanOpenMaster`; Gallery
  *CANopen I/O modules*; sample `CanOpenMaster`; notebook pair `industrial/16-canopen`; docs page *CANopen*.
- Tooling since 0.15: generated C headers and binding drift checks (`rust/tools/iotcom-bindgen`), CI header compile.

*CANopen (master, perangkat, SDO, PDO, NMT, heartbeat, emergency) dengan kembaran Rust yang di-fuzz, CLI, demo Galeri,
sampel, notebook, dan dokumentasi.*

## 0.15.0-preview.1 — 2026-10-09

USB CAN adapters.

- **New package `IoTCom.Net.Transport.Can.Adapters`** (also in the meta-package): `GsUsbCanBus` for candleLight / gs_usb
  firmware (CANable 2, CANtact Pro…) over `Transport.Usb`, with the gs_usb host protocol, bit-timing calculation from
  the adapter's clock and limits, TX echo handling and CAN FD when supported; `PcanCanBus` for PEAK PCAN-USB through
  PCANBasic (classic CAN, standard bit rates, listen-only); `VirtualGsUsbDevice` bridging a virtual candleLight to a
  `VirtualCanNetwork`.
- `CanBus.RegisterScheme` lets adapter packages add URI schemes; `gsusb:` and `pcan:` register themselves.
  `iotcom can list` finds candleLight and PCAN adapters, and every `--can` option accepts the new URIs.
- Notebook `automotive/06-can-uds` gains a gs_usb section; docs page *CAN / CAN FD* documents both adapters.

*Adapter CAN USB: candleLight/gs_usb lewat USB mentah dan PEAK PCAN-USB lewat PCANBasic, dengan skema URI `gsusb:` dan
`pcan:`.*

## 0.14.0-preview.1 — 2026-10-09

USB and HID.

- **New package `IoTCom.Net.Transport.Usb`** (also in the meta-package): `UsbDevice` (control, bulk and interrupt
  transfers, read-only switch, traffic tap), `UsbBulkTransport` / `UseUsbBulk`, `HidDevice` (input, output and feature
  reports), `HidRelayBoard` for "USBRelayN" modules, enumeration with `UsbIds` names, and `VirtualUsbBus` with a
  loopback device and a simulated relay board.
- **New native library `iotcom_usb`** (Rust crate `iotcom-usb-native`: nusb 0.2 for WinUSB/usbfs/IOKit, hidapi with
  pure-Rust backends on Windows and Linux), best effort per RID like `iotcom_ble`.
- CLI `iotcom usb list|hid|control|write|relay`; hosting `AddUsbDevice`, `AddHidDevice`; Gallery *USB bench*; sample
  `UsbRelay`; notebook pair `devices/15-usb`; docs page *USB and HID*.
- Tests: M-Bus session tests get a 1 s response window (an Alpine runner missed 150 ms on a cold start).

*Transport USB dan HID dengan pustaka native Rust (nusb, hidapi), papan relay USB HID, bus virtual, CLI, demo Galeri,
sampel, notebook, dan dokumentasi.*

## 0.13.0-preview.1 — 2026-10-09

Bluetooth Low Energy.

- **New package `IoTCom.Net.Transport.Ble`** (also in the meta-package): `BleCentral` (scan, connect, read, write behind a
  read-only switch, notifications as `IAsyncEnumerable`, traffic tap), codecs for advertising data, iBeacon, Eddystone
  and GATT values, and `VirtualBleNetwork` with a heart-rate strap, a greenhouse sensor, an iBeacon and a smart plug.
- **New native library `iotcom_ble`** (Rust crate `iotcom-ble-native` on btleplug 0.13: WinRT, BlueZ with vendored
  libdbus, CoreBluetooth), same C ABI contract as the other libraries; built per RID on a best-effort basis.
- CLI `iotcom ble scan|services|watch|write`; RPC decoder `ble-adv`; hosting `AddBleCentral`; Gallery *Nearby Bluetooth
  devices*; sample `BleHeartRate`; notebook pair `devices/14-ble`; docs page *Bluetooth LE*.
- Tests: wider timing bounds for the LoRaWAN RX1 test on busy CI runners.

*Central Bluetooth LE dengan pustaka native Rust (btleplug), codec advertisement/iBeacon/Eddystone/GATT, radio virtual,
CLI, demo Galeri, sampel, notebook, dan dokumentasi.*

## 0.12.0-preview.1 — 2026-10-09

OPC UA.

- **New package `IoTCom.Net.Adapters.OpcUa`** over the OPC Foundation .NET Standard stack 1.5.378 (MIT):
  `OpcUaClient` (most secure endpoint or None, anonymous or user name, browse, read, write with type conversion,
  method calls, subscriptions as `IAsyncEnumerable`, read-only mode, traffic tap) and `OpcUaPlantServer`, a bottling
  line simulator with None and Basic256Sha256 endpoints. Not in the meta-package (large, not trimming/AOT safe).
- CLI `iotcom opcua browse|read|watch|write|call|simulate`; Gallery *OPC UA tag browser*; sample `OpcUaBrowser`;
  notebook pair `industrial/13-opcua`; docs page *OPC UA* with the certificate trust workflow.

*Adapter OPC UA (client dan simulator server pabrik), perintah CLI, demo Galeri, sampel, notebook, dan dokumentasi.*

## 0.11.0-preview.1 — 2026-10-09

Plant networks: discovery, Sparkplug B and payload codecs.

- **New package `IoTCom.Net.Protocols.Mdns`:** DNS codec with name compression, `MdnsResponder` (announcements,
  multicast and unicast answers, known-answer suppression, goodbyes) and `MdnsBrowser` (TTL cache, back-off,
  service-type enumeration, `ServiceChanged`), plus `MdnsSimulator`. CLI `iotcom mdns browse|advertise`; sample
  `MdnsDiscovery`. Core: `UdpDatagramTransport.Multicast(group, port)` and multicast delivery on
  `InMemoryDatagramNetwork`.
- **New package `IoTCom.Net.Protocols.Sparkplug`:** Sparkplug B payload codec and topics, `SparkplugEdgeNode`
  (NDEATH will with bdSeq, births with aliases, data by exception, rebirth, guarded writes), `SparkplugHost` (STATE,
  alias resolution, sequence-gap and late-join rebirth, typed writes) and a bottling-line simulator. CLI
  `iotcom sparkplug watch|simulate|write`; sample `SparkplugEdgeNode`; hosting `AddSparkplugEdgeNode`,
  `AddSparkplugHost`, `AddMdnsResponder`, `AddMdnsBrowser`.
- **New packages `IoTCom.Net.Serialization.Protobuf`, `.MessagePack`, `.Tlv`** and the `IPayloadCodec<T>` contract
  (Abstractions), also implemented by `SenMLPayloadCodec`. Schema-less views (`ProtobufWire`, `MessagePackView`,
  `BerTlv.Describe`); CLI `iotcom payload <format> <hex>`; VS Code decoders `sparkplug`, `dns`, `protobuf`,
  `msgpack`, `ber-tlv`.
- **MQTT adapter:** binary will with retain and QoS (`WithWill(topic, bytes, retain, qos)`) and `AbortAsync()` to drop
  a connection so the broker publishes the will.
- Gallery *Plant network · Sparkplug B*; notebook pair `messaging/12-mdns-sparkplug`; docs *mDNS / DNS-SD*,
  *Sparkplug B*, *Payload codecs*.

*Penemuan mDNS/DNS-SD, Sparkplug B (edge node dan host application), adapter Protobuf/MessagePack, helper TLV,
kontrak `IPayloadCodec<T>`, will MQTT biner, demo Galeri jaringan pabrik.*

## 0.10.0-preview.1 — 2026-10-09

AIS, AT commands, ASTM — and the DLMS/M-Bus work, published.

- **0.9.0-preview.1 was tagged but never reached NuGet** (a macOS runner never started, so packing was skipped);
  its DLMS/COSEM and M-Bus packages ship in this release.
- **Fix (DLMS, macOS):** suite 0 ciphering and HLS failed on macOS, whose AES-GCM accepts only 16-byte tags. Tags are
  now computed in full and truncated to 12 bytes (decryption recomputes them), byte-identical to native 12-byte GCM.
- **NMEA: AIS.** `AisDecoder` reassembles `!AIVDM`/`!AIVDO` fragments and decodes types 1–5, 18, 19, 21, 24 and 27;
  `AisTracker` keeps a vessel table; `AisBits` encodes position and voyage reports; `AisSimulator` produces traffic
  in Jakarta Bay. CLI `iotcom nmea ais decode|watch`; Gallery *Harbour traffic (AIS)*.
- **New package `IoTCom.Net.Protocols.AtCommand`:** a sans-I/O AT response parser that separates URCs, `AtModem`
  (timeouts, URC stream, SMS text mode, identity/signal/registration, read-only mode) and `AtModemSimulator`
  (LTE-M module with PIN, +CEREG, SMS). CLI `iotcom at send|info|sms|simulate`.
- **New package `IoTCom.Net.Protocols.Astm`:** ASTM E1394 / LIS2-A2 records and the E1381 / LIS1-A link (ENQ/ACK,
  checksummed frames, ETB splitting, NAK retransmission), `AstmReceiver`, `AstmSender`, a synthetic chemistry
  analyzer and an ASTM → HL7 ORU^R01 bridge. CLI `iotcom astm listen|send`; sample `AstmAnalyzerBridge`.
- Notebooks: AIS cells in `navigation/03-nmea`, new pair `devices/11-at-astm`; docs pages *AT commands* and
  *ASTM E1394*, AIS section in *NMEA 0183*; hosting helpers `AddAtModem`, `AddAstmReceiver`.

*AIS (decoder, pelacak kapal, simulator, demo Galeri), mesin perintah AT, ASTM E1394 dengan jembatan HL7, perbaikan
enkripsi DLMS di macOS, dan paket DLMS/M-Bus yang belum sempat terbit di 0.9.0.*

## 0.9.0-preview.1 — 2026-10-09

Metering: DLMS/COSEM and M-Bus.

- **New package `IoTCom.Net.Protocols.Dlms` (IEC 62056):** HDLC links (SNRM/UA negotiation, segmentation with RR)
  and the TCP wrapper; A-XDR data, OBIS codes and catalog, COSEM date-time; AARQ/AARE with no, low (password) and
  high (GMAC) security and ciphered APDUs (suite 0, AES-GCM, replay protection); GET with block transfer and
  selective access, SET, ACTION; `DlmsClient` (read-only by default), `DlmsServer` with COSEM classes (Data,
  Register, Clock, Profile generic, Disconnect control, Association LN), and `DlmsMeterSimulator` (three-phase
  household with rooftop solar, PLN WBP/LWBP tariffs, 15-minute load profile, relay).
- **New package `IoTCom.Net.Protocols.MBus` (EN 13757-2/-3):** frame codec, variable data records (DIF/DIFE,
  VIF/VIFE, BCD, type F/G dates), `MBusMaster` (ping, REQ_UD2 with FCB, scan, secondary addressing) and
  `MBusSlaveSimulator` (heat, water and electricity meters).
- **Rust twins `iotcom-dlms` and `iotcom-mbus`** on shared vectors (`dlms.json`, `mbus.json`) from a separate Python
  reference; fuzz targets 8 and 9.
- **CLI:** `iotcom dlms read|objects|profile|relay|simulate`, `iotcom mbus scan|read|decode|simulate`; `iotcom rpc`
  gains `dlms`/`mbus` decoders and `sim:dlms`/`sim:mbus` monitors.
- **Gallery:** *Smart meter reading* — the meter's LCD faceplate, two days of load profile, the relay behind a
  password, and the building's M-Bus meters.
- Samples `DlmsMeterReader` and `MBusScanner`, notebook pair `metering/10-dlms-mbus`, hosting helpers
  `AddDlmsClient`/`AddMBusMaster`, docs pages *DLMS/COSEM* and *M-Bus*.
- **Fix:** ISO-TP round-trip tests had too little timeout headroom on busy CI runners.

*Metering: paket DLMS/COSEM (HDLC dan wrapper, OBIS, LLS/HLS dengan AES-GCM, block transfer, simulator meter
rumah tangga dengan panel surya) dan M-Bus berkabel (master, alamat sekunder, simulator), twin Rust yang di-fuzz,
perintah CLI, demo Galeri faceplate LCD, sampel, notebook, dan dokumentasi EN/ID.*

## 0.8.0-preview.1 — 2026-10-08

LoRaWAN.

- **New package `IoTCom.Net.Protocols.LoRaWan` (LoRaWAN 1.0.x):**
  - PHYPayload codec, AES-CMAC MICs, FRMPayload and Join-Accept encryption, OTAA key derivation, ABP;
  - MAC commands (1.0.4 Class A set) and regional parameters (EU868, US915 sub-band 2, AS923-2) with time on air;
  - the Semtech UDP packet-forwarder protocol on both sides (`SemtechPacket`, `SemtechPacketForwarder`), including
    the unpadded base64 that real forwarders send;
  - a light network server (`LoRaWanNetworkServer`): deduplication across gateways, join and replay checks, Class A
    downlinks in RX1 (ACKs, queued data, MAC answers), LinkCheck, DeviceTime, DevStatus;
  - a sans-I/O Class A end-device MAC, Cayenne LPP, and `LoRaWanSimulator` (gateways and sensors with a path-loss
    radio model).
- **Rust twin `iotcom-lorawan`** (RustCrypto AES/CMAC) runs the same vectors (`/conformance/lorawan.json`, produced
  by a self-checked pure-Python AES/CMAC reference) and is fuzzed (`cargo fuzz run lorawan`, 7th target).
- **pcapng:** LoRaWAN frames are written as LoRaTap (LINKTYPE 270), so Wireshark decodes them; the CI tshark gate
  checks it.
- **CLI:** `iotcom lorawan server` (`--sim`, `--devices`, `--pcap`), `simulate` (drive ChirpStack/TTS), `decode`, `airtime`;
  `iotcom rpc` gains the `lorawan`/`semtech-udp` decoders and the `sim:lorawan`/`lorawan:udp:<port>` monitors (VS Code).
- **Gallery:** *LoRaWAN network monitor* — a radio map with spreading-factor reach, draggable sensors, uplinks to
  every gateway that heard them and downlinks in RX1.
- Sample `LoRaWanGatewayMonitor`, notebook pair `lpwan/09-lorawan`, hosting helper `AddLoRaWanNetworkServer`, docs
  page *LoRaWAN*, benchmark (≈ 5 µs per uplink to encode, or to decode, verify and decrypt).

*LoRaWAN: paket baru (codec, kriptografi, MAC command, region, Semtech UDP, network server ringan, MAC end device,
simulator), twin Rust yang di-fuzz, capture pcapng LoRaTap, perintah `iotcom lorawan`, demo Galeri peta radio,
sampel, notebook, dan dokumentasi EN/ID.*

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
