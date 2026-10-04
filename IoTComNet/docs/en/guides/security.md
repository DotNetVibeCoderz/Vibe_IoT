---
title: Security and safety
translation-status: synced
---

# Security and safety

IoTCom.Net talks to equipment that moves, heats, switches and lights things. Treat writes as physical actions.

## Safe by default

- **Read-only mode.** `ModbusClient.AsReadOnly()` and `ModbusServer.AsReadOnly()` block every write. Writes on a
  read-only client throw `ReadOnlyModeException` before anything is sent.
- **The CLI asks twice.** `iotcom modbus write` refuses to run without `--allow-write` and then asks for confirmation
  (skip it only in scripts, with `--yes`).
- **The gateway can be locked.** `Gateway:AllowWrites=false` disables the dashboard controls and the write endpoints.

## Untrusted input

Every parser treats network and device input as untrusted: lengths are bounded (Modbus PDU ≤ 253 bytes, frames
limited per codec), receive buffers are capped, malformed frames are dropped and the stream resynchronises. The Rust
codecs are additionally exercised with a randomised decoder test, and fuzz targets are part of the roadmap.

## Transport security

| Protocol | Recommendation |
|---|---|
| Modbus | no security in the protocol: isolate the network (VLAN/firewall), never expose 502 to the internet, put a gateway in front |
| MQTT | TLS (`WithTls()`, port 8883), credentials, broker ACLs per topic |
| Art-Net / sACN | separate lighting network; unicast where possible |
| NMEA | treat positions as untrusted in safety-relevant systems |

Credentials are never logged. Traffic taps capture payloads — protect captures like the data they contain.

## Supply chain

- `cargo deny` / `cargo audit` and `dotnet list package --vulnerable` run in CI.
- Dependencies are pinned centrally (`Directory.Packages.props`, `Cargo.toml` workspace).
- No GPL/AGPL dependencies in core packages; licences are reviewed before adding a crate or package.

Report vulnerabilities as described in [SECURITY.md](../../../SECURITY.md).
