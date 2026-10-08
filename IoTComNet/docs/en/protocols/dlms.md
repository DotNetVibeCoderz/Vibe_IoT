---
title: DLMS/COSEM
translation-status: synced
---

# DLMS/COSEM

**Summary.** DLMS/COSEM (IEC 62056) is how electricity meters, and many gas and water meters, are read and managed.
A meter exposes **COSEM objects**, each named by an **OBIS code** (`1-0:1.8.0*255` is active energy imported). A
client opens an **association**, then GETs attributes, SETs them or invokes methods. IoTCom.Net implements:

- both links: **HDLC** (IEC 62056-46, optical port and RS-485) with segmentation, and the **TCP wrapper**
  (IEC 62056-47, port 4059);
- **A-XDR** data, OBIS codes with a catalog, registers with scaler and unit, the COSEM date-time;
- associations with **no**, **low** (password) and **high** (GMAC, mechanism 5) security, and **ciphered APDUs**
  (security suite 0, AES-GCM) with replay protection;
- **GET** with block transfer and selective access, **SET** and **ACTION**;
- a meter **server** and a **simulator**: a three-phase household meter with rooftop solar, PLN time-of-use tariffs,
  a 15-minute load profile and a supply relay.

![Smart meter reading](../../images/gallery-metering.png)

## When to use it

- Reading meters on a desk or in the field through an optical probe, an RS-485 bus or a TCP gateway.
- Building head-end or data-collector software that collects registers and load profiles.
- Testing your software against a meter you control, including wrong passwords, denied writes and long profiles.

## Roles

| Role | Type |
|---|---|
| Client (meter reader) | `DlmsClient`: `GetAsync`, `SetAsync`, `ActionAsync`, `ReadRegisterAsync`, `ReadClockAsync`, `ReadObjectListAsync`, `ReadProfileAsync` |
| Server (meter) | `DlmsServer` with `CosemObject`s: `CosemRegister`, `CosemClock`, `CosemProfileGeneric`, `CosemDisconnectControl`, `CosemDataObject`, `CosemAssociationLn` |
| Simulator | `DlmsMeterSimulator`: energy (import/export, per tariff), per-phase values, clock, load profile, relay |
| Codec | `ObisCode`, `CosemData`, `HdlcFrame`, `DlmsWrapper`, `DlmsApdu`, `DlmsSecurity`, `DlmsAnatomy` (sans-I/O) |

## Associations and access

| Client | SAP | Authentication | May |
|---|---|---|---|
| Public client | 16 | none | read |
| Management client | 1 | password (LLS), `WithPassword("…")` | read, write, invoke |
| Management client | 1 | GMAC challenge-response (HLS 5) with ciphered APDUs, `WithHighSecurity(keys)` | read, write, invoke |

The client is **read-only by default**: `SetAsync` and `ActionAsync` throw `ReadOnlyModeException` until you set
`ReadOnly = false`. The meter enforces its own rules as well, and answers `ReadWriteDenied` to a public client.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Dlms --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Transport.Serial;

await using var meter = DlmsClient.Create(o => o.UseSerial("COM3", 9600));   // optical probe, HDLC, public client
await meter.ConnectAsync();
Console.WriteLine(await meter.ReadClockAsync());
Console.WriteLine(await meter.ReadRegisterAsync(ObisCode.Parse("1-0:1.8.0*255")));   // 4842792 Wh

var now = await meter.ReadClockAsync();
var profile = await meter.ReadProfileAsync(ObisCode.Parse("1.0.99.1.0.255"), now.AddDays(-1), now);
foreach (var row in profile.Rows) Console.WriteLine(string.Join("  ", row));
```

Over TCP, use `o.UseTcp(host, 4059)` with `o.Framing = DlmsFraming.Wrapper` (or keep HDLC for gateways that
tunnel it). Set `ServerPhysicalAddress` to the meter's HDLC lower address (often 16 + the last digits of the serial
number).

## Serving a meter

```csharp
await using var server = DlmsServer.Create(o => { o.UseTcp(IPAddress.Any, 4059); o.Framing = DlmsFraming.Wrapper; });
server.Add(new CosemRegister(ObisCode.Parse("1.0.1.8.0.255"), () => energyWh, scaler: 0, CosemUnit.WattHour));
server.Add(new CosemClock(ObisCode.Parse("0.0.1.0.0.255"), () => DateTimeOffset.Now));
await server.StartAsync();
```

Or let `new DlmsMeterSimulator(server)` populate a complete meter. The association object (0.0.40.0.0.255) and its
object list are added automatically.

## How the protocol layers fit

| Layer | What happens |
|---|---|
| HDLC | SNRM/UA negotiates the information field size and window; I-frames carry `E6 E6 00` + APDU; long APDUs are segmented and each segment is acknowledged with RR; DISC ends the link |
| Wrapper | 8-byte header (version 1, source and destination wPort, length) before each APDU |
| ACSE | AARQ/AARE: application context (LN, with or without ciphering), mechanism, password or challenge, conformance block, maximum PDU size |
| xDLMS | GET/SET/ACTION; long GET results return in blocks (`GET.response with-datablock`) that the client requests one by one |
| Security | ciphered APDUs: tag, security control `0x30`, invocation counter, AES-GCM ciphertext and a 12-byte tag; the IV is the sender's system title and the counter |

## Tools

```bash
iotcom dlms read --sim                                 # simulated meter
iotcom dlms read --serial COM3 1.0.1.8.0.255 1.0.32.7.0.255
iotcom dlms objects -h 10.0.0.30                        # wrapper on TCP 4059
iotcom dlms profile --sim --hours 6
iotcom dlms relay off --sim --password 12345678 --allow-write
iotcom dlms simulate --port 4059 --frames
dotnet run --project samples/console/DlmsMeterReader
```

## Testing and interoperability

- **Codec:** C# and the Rust crate `iotcom-dlms` run the same vectors (`/conformance/dlms.json`): HDLC frames (the
  classic SNRM `7E A0 07 03 21 93 0F 01 7E` included, bad HCS and FCS) and A-XDR values in a canonical text form. A
  separate Python implementation produces them. The Rust codec is fuzzed (`cargo fuzz run dlms`).
- **APDUs:** the public-client AARQ matches a published reference byte for byte; GET, block and ciphered PDUs round trip.
- **Sessions:** HDLC and wrapper, public client reads (registers, clock, object list over block transfer and 64-byte
  segments, profile by date range), LLS writes and a rejected password, HLS with every APDU ciphered, wrong keys
  refused.

## Security

Passwords and keys are secrets: never log or commit them (the examples use published test keys). LLS sends the
password in clear text; prefer HLS with ciphering on any network you do not control. Keep readers read-only, and
treat switching a relay as an operation on a customer's supply. The CLI requires `--allow-write` and a confirmation.

## Limitations

Logical-name referencing only (no short names). Not yet included: security suites 1 and 2 (ECDSA, ECDH), dedicated
keys, general-block-transfer and general-protection, data notifications and push, IEC 62056-21 mode E handshake
(start the meter in HDLC), image transfer, and a full Blue Book class library.

## Learn more

Gallery demo *Smart meter reading* · notebook `notebooks/metering/10-dlms-mbus.en.ipynb` · [M-Bus](mbus.md) ·
`iotcom dlms --help`
