---
title: CANopen
translation-status: synced
---

# CANopen (CiA 301)

**Summary.** CANopen gives CAN networks a device model. Drives, I/O modules, encoders, sensors, battery systems and
medical devices expose an object dictionary addressed by index and sub-index. A master configures and reads it with
SDO, exchanges process data in PDOs, manages node states with NMT and supervises them with heartbeats.
`IoTCom.Net.Protocols.CanOpen` runs on any `ICanBus` (SocketCAN, slcan, gs_usb, PCAN, virtual) and provides:

- `CanOpenMaster`:
  - NMT commands and a heartbeat consumer (`NodeStateChanged`)
  - SDO client: expedited and segmented, typed `ReadAsync`/`WriteAsync`, `CanOpenSdoException` with the CiA abort code
  - SYNC, `PdoReceived` / `EmergencyReceived` events, `ReadTpdoMappingAsync`, and `ScanAsync` (device type, name and
    identity of every node that answers)
  - a read-only mode that refuses SDO downloads and NMT
- `CanOpenNode`: a device.
  - boot-up and the NMT state machine, heartbeat producer (0x1017)
  - SDO server with aborts for missing objects, read-only/write-only access and length mismatches
  - TPDOs (event-driven with event timers, or every n-th SYNC) and RPDOs written into the dictionary
  - emergencies
- `ObjectDictionary` with the standard communication objects, `DefinePdo`, and `PdoMapping`.
- `CanOpenCodec`: COB-IDs, SDO frames and the frame lane.
- `CanOpenIoModuleSimulator`: a CiA 401-style I/O module (8 DI, 8 DO, 2 AI, temperature).

The codec has a fuzzed Rust twin (`rust/crates/iotcom-canopen`) checked against the same `/conformance/canopen.json`
vectors, which come from an independent Python reference.

## When to use it

- Commissioning and monitoring CANopen devices from a PC, a gateway or a test bench.
- Bridging CANopen process data to MQTT, OPC UA or a database.
- Building a CANopen device in .NET, or simulating one to test a PLC or HMI.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.CanOpen --prerelease    # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.CanOpen;
using IoTCom.Net.Transport.Can;

await using var bus = await CanBus.OpenAsync("socketcan:can0");          // or slcan:COM5, gsusb:, pcan:usb1
await using var master = CanOpenMaster.Create(bus, o => o.ReadOnly = true);
await master.StartAsync();

foreach (var n in await master.ScanAsync()) Console.WriteLine($"{n.Id}: {n.Name} vendor 0x{n.Identity?.Vendor:X8}");
var name = await master.ReadAsync(5, 0x1008, 0, CanOpenDataType.VisibleString);   // segmented when > 4 bytes
var map = await master.ReadTpdoMappingAsync(5, 1);
master.PdoReceived += (node, pdo, data) => Console.WriteLine(string.Join(" ", map.Unpack(data).Select(v => $"{v.Object}={Convert.ToHexString(v.Raw)}")));
```

A device:

```csharp
var od = ObjectDictionary.CreateStandard(0x000F0191, "My IO", vendorId: 0xABC, productCode: 1, revision: 1, serial: 42);
od.Add(0x6000, 1, "Inputs", CanOpenDataType.Unsigned8, CanOpenAccess.ReadOnly, (byte)0, pdoMappable: true);
od.DefinePdo(transmit: true, 1, CanOpenCodec.TpdoCobId(1, 10), [(0x6000, 1)], transmissionType: 0xFF, eventTimerMs: 100);
await using var node = CanOpenNode.Create(bus, nodeId: 10, od);
await node.StartAsync();                    // boot-up, pre-operational; the master's NMT start makes it send PDOs
od.Set(0x6000, 1, (byte)0b101);             // event-driven TPDO goes out
```

## Safety

NMT and SDO downloads change what machines do. `ReadOnly = true` makes `DownloadAsync`, `WriteAsync` and `NmtAsync`
throw `ReadOnlyModeException`. `iotcom canopen write` and `iotcom canopen nmt` need `--allow-write`, and `write` asks
for confirmation on real buses.

## Tools

```bash
iotcom canopen scan --can sim                       # two simulated I/O modules (nodes 5 and 6)
iotcom canopen read 5 1008 -t str --can sim
iotcom canopen write 5 6200:01 1 -t u8 --can sim --allow-write
iotcom canopen nmt start 0 --can socketcan:can0 --allow-write
iotcom canopen monitor --can gsusb:                 # heartbeats, PDOs and emergencies
```

The VS Code extension decodes CANopen frames (`canopen`). Gallery: *CANopen I/O modules*.

## Testing

Codec tests use CiA 301 byte examples: expedited reads of 1 to 4 bytes, the producer heartbeat write, aborts, and
a segmented upload with toggling segments. They also cover COB-ID classification for every function and PDO number,
PDO mapping entries and EMCY. Session tests run a master and the simulated module on a virtual network:
- scan with identity, segmented SDO in both directions, and every abort the server produces;
- a node that does not answer, and stopped nodes ignoring SDO;
- NMT transitions, TPDOs on the event timer and on SYNC, an RPDO switching an output;
- emergencies, reset to pre-operational, and the read-only master.

The 71 shared vectors run in both the C# and the Rust test suites. The Rust codec was fuzzed for 7 million runs with
no findings.

## Limitations

There is no SDO block transfer (decoded as unsupported), LSS, node guarding, time stamp producer or EDS/DCF import
yet. PDO mapping is byte-aligned. Heartbeat consumer timeouts are reported through `LastSeen` rather than an event.

## Learn more

Notebook `notebooks/industrial/16-canopen.en.ipynb` · sample `samples/console/CanOpenMaster` · [CAN / CAN FD](can.md)
