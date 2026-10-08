---
title: M-Bus
translation-status: synced
---

# M-Bus (wired)

**Summary.** The Meter-Bus (EN 13757-2/-3) connects heat, water, gas and electricity sub-meters to a master over two
wires, at 300–9600 baud (usually 2400, 8E1). A master addresses each slave by its primary address (1–250) or by its
secondary address (ID, manufacturer, version, medium), and reads **variable data records**. IoTCom.Net implements:

- the **frame codec**: E5, short, control and long frames with checksums;
- **variable data** (CI 0x72): identification, manufacturer, medium, access number, status, and records with
  DIF/DIFE (data coding, function, storage, tariff, sub-unit) and VIF/VIFE (quantity, unit, power of ten), BCD, and
  type F/G dates;
- a **master**: SND_NKE ping, REQ_UD2 with the frame count bit, primary scan, secondary-address selection with
  wildcards;
- a **slave simulator**: a segment with a heat meter, a water meter and an electricity meter.

## When to use it

- Collecting heat, water and energy readings in buildings (sub-metering, cost allocation).
- Commissioning a segment: find every slave and check its records.
- Testing a data collector without the basement.

## Roles

| Role | Type |
|---|---|
| Master | `MBusMaster`: `PingAsync`, `ReadAsync`, `ScanAsync`, `SelectAsync`, `ReadSecondaryAsync` |
| Slaves | `MBusSlaveSimulator` with `MBusSimulatedDevice`s (`AddDefaultDevices()` adds three meters) |
| Codec | `MBusFrame`, `MBusTelegram`, `MBusRecord`, `MBusRecordWriter`, `MBusVif`, `MBusAnatomy` (sans-I/O) |

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.MBus --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using System.IO.Ports;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transport.Serial;

await using var bus = MBusMaster.Create(o => o.UseSerial("COM4", 2400, Parity.Even));
await bus.ConnectAsync();
foreach (var address in await bus.ScanAsync())
{
    var telegram = await bus.ReadAsync(address);
    Console.WriteLine($"{telegram.SecondaryAddress} {telegram.MediumName}");
    foreach (var record in telegram.Records) Console.WriteLine($"  {record}");   // Volume: 12.565 m³
}

var water = await bus.ReadSecondaryAsync("26200002");   // select by ID, read through address 253
```

## Reading a record

| Byte | Meaning |
|---|---|
| DIF | data field (int8…int64, real32, BCD 2–12 digits, variable length), function (instantaneous, max, min, error), storage bit, extension bit |
| DIFE | more storage bits, tariff, sub-unit |
| VIF | quantity and unit with a power of ten (energy Wh·10ⁿ⁻³, volume m³·10ⁿ⁻⁶, flow temperature °C·10ⁿ⁻³, date type G, date-time type F, …) |
| VIFE / `0xFD` | extensions such as error flags, voltage and current |

`MBusRecord` gives the quantity, unit, scaled `Value`, `Time` for dates, storage number, tariff and sub-unit.

## Tools

```bash
iotcom mbus scan --sim --to 10
iotcom mbus scan --serial COM4               # 2400 8E1 through a level converter
iotcom mbus read 1 -h 10.0.0.40 -p 10001     # through an M-Bus/TCP gateway
iotcom mbus read 26200002 --sim              # secondary address
iotcom mbus decode 681F1F680802727856341224400107550000000313153100DA023B13018B60043718021816
iotcom mbus simulate --port 10001
dotnet run --project samples/console/MBusScanner
```

## Testing and interoperability

- **Codec:** C# and the Rust crate `iotcom-mbus` run the same vectors (`/conformance/mbus.json`): the water-meter
  telegram from the M-Bus documentation (checksum 0x18), records with storage, tariff, sub-unit, fillers and
  manufacturer data, short and selection frames, and corrupted frames. The Rust codec is fuzzed (`cargo fuzz run mbus`).
- **Sessions:** scan, reads with the access number advancing, secondary addressing with wildcards, an ambiguous
  wildcard that would collide on a real bus, and absent slaves.

## Security

Wired M-Bus has no security: anyone on the segment can read it. Encryption (mode 5/7) belongs to wireless M-Bus and
OMS, which are not covered yet. Reading never changes a meter; this library sends SND_UD only to select a slave.

## Limitations

Not yet included: wireless M-Bus (EN 13757-4) and OMS encryption, writing primary addresses and other SND_UD
commands, REQ_UD1 alarms, multi-telegram reads (DIF 0x1F is reported), baud-rate switching, and the complete VIFE
table (combinable extensions are kept as raw bytes).

## Learn more

Gallery demo *Smart meter reading* · notebook `notebooks/metering/10-dlms-mbus.en.ipynb` · [DLMS/COSEM](dlms.md) ·
`iotcom mbus --help`
