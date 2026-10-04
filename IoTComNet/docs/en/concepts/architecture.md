---
title: Architecture
translation-status: synced
---

# Architecture

## Three decisions

1. **Strict curation.** What .NET already does well is not rebuilt. TCP, UDP, TLS, QUIC, HTTP, WebSocket, gRPC, serial
   ports, GPIO, JSON, CBOR and cryptography come from the BCL or official Microsoft packages. Mature community libraries
   are wrapped as adapters. Only real gaps are implemented.
2. **Rust for the low level.** Hardware access and complex binary state machines that deserve fuzzing and memory safety
   are written in Rust and exposed through a C ABI. Public API, DI, async and ASP.NET Core integration stay in C#.
3. **A complete experience.** Library, CLI, samples, the Gallery, notebooks, templates and bilingual docs ship together.

## Curation tiers

| Tier | Meaning | Action | Examples |
|---|---|---|---|
| **T0** BCL / official | Already in .NET | Not rebuilt; used as transport | `System.Net.Sockets`, `SslStream`, `System.IO.Ports`, `System.Formats.Cbor` |
| **T1** Adapter | Mature .NET library exists | Wrapped behind IoTCom contracts | MQTT → MQTTnet |
| **T2** Implementation | Real gap | Built in Rust (T2-R) or C# (T2-C) | Modbus (C# + Rust engine), NMEA, Art-Net, sACN, SenML, framing |
| **T3** Out of scope | Radio/PHY/firmware | Documented only | LoRa PHY, Zigbee radio, 5G |

## Layers

```
┌───────────────────────────────────────────────────────────────────────────┐
│ Apps: Gallery (Avalonia) · Gateway (ASP.NET Core) · console samples · notebooks
├───────────────────────────────────────────────────────────────────────────┤
│ Tooling: iotcom CLI · dotnet new templates · screenshot & docs tooling     │
├───────────────────────────────────────────────────────────────────────────┤
│ IoTCom.Net.Hosting — AddIoTCom(), hosted lifecycle, health checks          │
├───────────────────────────────────────────────────────────────────────────┤
│ Protocols (C#): Modbus · NMEA · Art-Net/sACN · SenML · Framing             │
│ Adapters (T1):  MQTT (MQTTnet)                                             │
├───────────────────────────────────────────────────────────────────────────┤
│ IoTCom.Net.Core — ITransport over System.IO.Pipelines, EndpointBase,       │
│                   traffic tap, diagnostics, reconnect, native loader       │
├─────────────────────────────────┬─────────────────────────────────────────┤
│ Transports (BCL): TCP · serial  │ Native bridge: LibraryImport · SafeHandle│
│ · UDP · in-memory               │ · RID loader · ABI version check         │
├─────────────────────────────────┴─────────────────────────────────────────┤
│ Rust: iotcom-core (Machine) · iotcom-ffi-support · iotcom-modbus · cdylibs │
└───────────────────────────────────────────────────────────────────────────┘
```

## Sans-I/O

Protocol logic never touches sockets or ports. A protocol *machine* receives bytes, commands and timer ticks, and
produces bytes to send, events and its next deadline:

```rust
pub trait Machine {
    type Event;
    type Command;
    fn handle_input(&mut self, now: Instant, bytes: &[u8]) -> Result<()>;
    fn handle_command(&mut self, now: Instant, cmd: Self::Command) -> Result<()>;
    fn handle_timeout(&mut self, now: Instant);
    fn poll_transmit(&mut self, out: &mut Vec<u8>) -> Option<Transmit>;
    fn poll_event(&mut self) -> Option<Self::Event>;
    fn poll_timeout(&self) -> Option<Instant>;
}
```

Consequences: any transport works unchanged (TCP, serial, TLS, a replay file, an in-memory pipe), tests are
deterministic (time is a parameter), no async runtime is needed on the Rust side, and the FFI surface stays narrow.
The managed C# protocols follow the same split: codecs (`ModbusPdu`, `ModbusFraming`, `NmeaParser`, `ArtNetPacket`)
are pure functions; endpoints only drive I/O.

## Rust or C#?

**Rust** when one of these holds: OS/hardware access missing from the BCL (CAN, USB, BLE, raw Ethernet, SWD); a
complex binary codec or state machine worth fuzzing (Modbus engine, DLMS, IEC 104, MAVLink, ISO-TP/UDS); a mature
crate with a compatible licence; or a hot path.

**C#** for text/JSON/HTTP protocols, simple UDP/TCP protocols (Art-Net, sACN, NMEA), small stateless helpers (CRC,
COBS, SLIP — an FFI call costs more than the function), adapters, and every public API.

Small helpers that exist on both sides are kept in sync by **shared test vectors** in `/conformance`, executed by both
the C# and the Rust test suites. A cross-language test also checks that the Rust Modbus engine and the managed framing
produce byte-identical frames.

## Packages and the meta-package

One package per protocol, so applications carry only what they use; `IoTCom.Net` bundles the common set. The Rust
engine ships separately (`IoTCom.Net.Native.Modbus`) with one native library per RID under `runtimes/{rid}/native/`
(one `cdylib` per protocol package, `iotcom-core` linked statically — ADR-003).

See also: [Endpoints & transports](endpoints-and-transports.md) · [Native layer](../native/rust-ffi.md)
