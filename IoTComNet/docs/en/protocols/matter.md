---
title: Matter
translation-status: synced
---

# Matter (controller, device and bridge)

**Summary.** Matter is the Connectivity Standards Alliance's smart-home standard: IPv6 devices that a controller
commissions once — proving it knows the setup passcode, checking the device's attestation and giving it an operational
certificate — and then reads, writes, commands and subscribes to over encrypted sessions. `IoTCom.Net.Protocols.Matter`
implements both sides in managed C#:

- **Codecs** (sans-I/O): `Tlv`/`TlvElement` (every element type and tag form), `MatterSetupPayload` (QR `MT:` base38 and
  11/21-digit manual codes with the Verhoeff check digit), `MatterMessage` (message and protocol headers, AES-CCM
  nonce and encryption), `StatusReport`, `InteractionModel` (Read, Write, Invoke, Subscribe, Report and Status messages,
  attribute and command paths with wildcards), `MatterCertificate` (Matter TLV operational certificates with the exact
  X.509 DER they sign, issuing, CSRs, compressed fabric id and IPK).
- **Security**: `Spake2Plus` (PASE: SPAKE2+ over P-256), CASE (SIGMA with operational certificates), session keys, message
  counters with duplicate detection, MRP reliability (acknowledgements and retransmissions), device attestation
  (`MatterAttestation`: DAC → PAI → PAA chains and signatures).
- **`MatterController`**: a fabric (root CA, its own NOC, IPK; optionally kept in a `FabricFile`), commissioning —
  PASE, fail-safe, attestation, CSR, root and NOC, CASE, CommissioningComplete — with step-by-step progress, then
  `MatterNode`: `ReadAsync` (wildcards), `WriteAttributeAsync`, `InvokeAsync`, `SubscribeAsync`. **Read-only by default**:
  commissioning, writes and commands need `AllowWrites()`.
- **`MatterDevice`**: endpoint 0 (Descriptor, Basic Information, General Commissioning, Operational Credentials, Access
  Control), your endpoints and clusters (`MatterCluster`, ready-made On/Off, Level Control, Temperature Measurement,
  Bridged Device Basic Information), PASE and CASE responders, a fabric table, the Interaction Model server with
  subscriptions, fail-safe roll-back and an admin-only access check.
- **`MatterBridge`** (Aggregator plus bridged endpoints) and **`MatterBridgeSimulator`** (a house: four lights, two
  temperature sensors, a dimmable lamp).
- **Discovery**: `MatterDiscovery` advertises and browses `_matterc._udp` (commissionable) and `_matter._tcp`
  (operational) through IoTCom's mDNS.

The codecs have a fuzzed Rust twin, `iotcom-matter`, and the shared `/conformance/matter.json` (41 vectors) comes from an
independent Python reference that also computes a full SPAKE2+ exchange and the spec's compressed-fabric-id example.

## When to use it

- Building a Matter bridge for devices on another protocol (KNX, Modbus, a proprietary bus) in .NET.
- Test tools: commission, read and drive devices from scripts, CI or a lab bench; a scriptable device to test controllers.
- Learning Matter end to end with every message visible (traffic tap, frame lanes).

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Matter --prerelease    # separate package: not in the meta-package
```

## Quickstart

A controller that commissions a device from its manual code and switches a light:

```csharp
using IoTCom.Net.Protocols.Matter;

await using var controller = MatterController.Create(o => o.UseUdp().AllowWrites().TrustTestDevices().FabricFile = "fabric.json");
var node = await controller.CommissionAsync(new IPEndPoint(IPAddress.Parse("192.168.1.40"), 5540),
    MatterSetupPayload.FromManualCode("34970112332"), nodeId: 1);
await node.InvokeAsync(1, MatterIds.OnOff, 1);                                   // On
var onOff = await node.ReadAttributeAsync(1, MatterIds.OnOff, 0);
await node.SubscribeAsync([new AttributePath(null, MatterIds.OnOff, 0)], 0, 60, r => Console.WriteLine($"{r.Path} = {r.Value}"));
```

A device with one dimmable light:

```csharp
await using var device = MatterDevice.Create(o => o.UseUdp(5540));
var ep = device.AddEndpoint(new MatterEndpoint(1, (MatterIds.DimmableLight, 3)));
var onOff = ep.Add(new MatterClusters.OnOffCluster());
ep.Add(new MatterClusters.LevelControlCluster(128, onOff));
await device.StartAsync();
Console.WriteLine(device.SetupPayload.ToManualCode());
```

A bridge for devices on another protocol:

```csharp
var bridge = new MatterBridge(device);
var (endpoint, kitchen, info) = bridge.AddLight("Kitchen", "knx-1/1/2");
kitchen.Changed += (_, _) => knx.WriteAsync("1/1/2", kitchen.On);               // forward Matter commands
info.Reachable = false;                                                          // the bridged device went away
```

Hosting: `AddMatterController` and `AddMatterDevice` (in this package).

## Safety

Commissioning adds a fabric — an administrator — to a device; writes and commands change the home. The controller is
read-only until `AllowWrites()`; the CLI needs `--allow-write`; the Gallery has a write lock. The fabric file holds the
root CA's private key: anyone with it can administer every device on the fabric. Keep it out of source control and
readable only by you. The test attestation (`MatterAttestation.TestPaa`, `TrustTestDevices()`) is published on purpose so
simulated devices work across processes — never use it for a real product.

## Interoperability status

Verified: the CHIP SDK's published onboarding example (`MT:Y.K9042C00KA0648G00`, `34970112332`), the spec's
compressed-fabric-id example, an independent Python implementation of SPAKE2+, TLV, payloads and headers, and IoTCom's
controller and device across processes over real UDP. **Not yet verified against other Matter stacks** (the CHIP SDK's
chip-tool, Apple Home, Google Home, Home Assistant, SmartThings); treat interoperability as experimental until it is.
Selling a Matter product needs CSA certification and a production attestation chain (DAC from an approved PAA and a
CSA-signed Certification Declaration); the Certification Declaration is not verified by this controller.

## Tools

```bash
iotcom matter code 34970112332                         # decode a QR or manual code (or encode one)
iotcom matter device [--bridge] [--mdns]               # a simulated light or bridge on UDP 5540
iotcom matter discover
iotcom matter commission 192.168.1.40 34970112332 --node 1 --allow-write [--trust-test]
iotcom matter read 192.168.1.40 1 0/0x0028/*
iotcom matter onoff 192.168.1.40 1 1 toggle --allow-write
iotcom matter subscribe 192.168.1.40 1 */0x0006/0
iotcom matter demo --allow-write
iotcom matter decode <hex>
```

The CLI keeps its fabric in `%APPDATA%/IoTCom.Net/matter/fabric.json` (or `--fabric`). Captures map Matter to UDP 5540;
the VS Code extension decodes `matter` (messages) and `matter-tlv`. Gallery: *Smart home over Matter*.

## Testing

Codec tests: the SDK's QR and manual codes, invalid codes, every TLV type, SPAKE2+ with right and wrong passcodes,
AES-CCM and ECDSA, operational certificates (TLV round trip, DER that .NET loads as X.509 and whose signature the root key
verifies). The 41 shared vectors cover TLV (including malformed input), QR and manual codes, message headers, the
compressed fabric id and two complete SPAKE2+ runs; the Rust twin is fuzzed (`matter`, about 4 M local runs).
End-to-end tests commission a light and a bridge in memory and check reads with wildcards, commands, constraint errors,
writes, subscriptions (priming and change reports), DNS-SD before and after commissioning, a fabric file across a
restart, a wrong passcode, an untrusted device, a read-only controller and a stranger fabric.

## Limitations

On-network commissioning only (no BLE, Wi-Fi or Thread network provisioning), no groups or group keys, no events, no
ICD/sleepy devices, no OTA, Access Control is read-only with the admin entry, no timed interactions, no chunked reports,
no session resumption, one fabric per controller instance. Only the clusters listed above are built in.

## Learn more

Notebook `notebooks/building/31-matter.en.ipynb` · sample `samples/console/MatterHome` · [mDNS](mdns.md) ·
[DTLS](dtls.md)
