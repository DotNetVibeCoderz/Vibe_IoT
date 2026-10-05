---
title: MAVLink
translation-status: synced
---

# MAVLink

**Summary.** MAVLink is the telemetry and command protocol of PX4, ArduPilot and most drones, rovers, boats and
gimbals. IoTCom.Net provides:

- the **complete common dialect** (235 messages), generated from the official XML by a **Roslyn source generator**
  that you can also run on your own dialects;
- a v1/v2 frame codec with MAVLink 2 truncation and **signing**;
- connections over UDP, TCP or serial;
- a **ground-station helper** (acknowledged commands, the parameter protocol, read-only mode);
- a **quadcopter simulator**.

![Drone telemetry over MAVLink](../../images/gallery-mavlink.png)

> **Safety.** Commands move real vehicles. Test with the simulator or SITL first, keep propellers off on the bench,
> and use read-only mode for monitoring. The CLI requires `--allow-write` for commands and parameter changes.

## When to use it

- Building a ground station, dashboard or logger for PX4/ArduPilot vehicles.
- Bridging telemetry to MQTT, a database or the cloud from a companion computer.
- Writing a MAVLink component (camera, sensor, payload) with your own messages.
- Testing tools against a simulated vehicle, without SITL or hardware.

## Roles

| Role | Type |
|---|---|
| Link (client / publisher / subscriber) | `MavlinkConnection`: `SendAsync`, `ReadAllAsync`, `WaitForAsync<T>`, `PacketReceived`, peers, loss statistics, heartbeats |
| Ground station | `MavlinkGroundStation`: `CommandAsync`, `ArmAsync`, `TakeoffAsync`, `LandAsync`, `ReturnToLaunchAsync`, `ReadParametersAsync`, `SetParameterAsync`, `State`, `StatusText` |
| Vehicle simulator | `MavlinkVehicleSimulator`: ArduCopter-like quadcopter (telemetry streams, commands, pre-arm checks, parameters) |
| Codec | `MavlinkCodec.Encode`, `MavlinkParser` (streaming, resync), `MavlinkSigning`, `MavlinkAnatomy` (sans-I/O) |
| Messages | `IoTCom.Net.Protocols.Mavlink.Common`: `Heartbeat`, `Attitude`, `GlobalPositionInt`, `CommandLong`, … and every enum (`MavCmd`, `MavType`, …) |

## Transports

| Setup | Code |
|---|---|
| Ground station on UDP 14550 (answers whoever spoke last) | `o.UseUdp(14550)` |
| Vehicle / companion sending to a GCS | `o.UseUdp(14555).SendTo(new IPEndPoint(gcs, 14550))` |
| SITL or MAVProxy over TCP | `o.UseTcp("127.0.0.1", 5760)` |
| Telemetry radio / flight controller USB | `o.UseSerial("COM7", 57600)` |
| Tests | `o.UseInMemory(network, address)` or `o.UseInMemory(transport)` |

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Mavlink --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;

await using var link = MavlinkConnection.Create(o => o.UseUdp(14550));
await link.ConnectAsync();

await foreach (var p in link.ReadAllAsync())
{
    switch (p.Message)
    {
        case Attitude a: Console.WriteLine($"roll {a.Roll * 57.3:0.0}°  pitch {a.Pitch * 57.3:0.0}°"); break;
        case GlobalPositionInt g: Console.WriteLine($"{g.Lat / 1e7:0.000000}, {g.Lon / 1e7:0.000000}  {g.RelativeAlt / 1000.0:0.0} m"); break;
        case Statustext t: Console.WriteLine($"[{t.Severity}] {t.Text}"); break;
    }
}
```

## Ground station

```csharp
using var gcs = new MavlinkGroundStation(link, targetSystem: 1);
await gcs.WaitForHeartbeatAsync(TimeSpan.FromSeconds(5));

var result = await gcs.ArmAsync();               // COMMAND_LONG 400 → COMMAND_ACK (retried with confirmation++)
if (result == MavResult.Accepted) await gcs.TakeoffAsync(15);

var parameters = await gcs.ReadParametersAsync(); // complete even on lossy links (missing indices are re-requested)
await gcs.SetParameterAsync("RTL_ALT", 2000);     // confirmed by the echoed PARAM_VALUE

Console.WriteLine($"{gcs.State.RelativeAltitude:0.0} m, {gcs.State.BatteryVoltage:0.00} V, armed: {gcs.State.Armed}");
```

Set `gcs.ReadOnly = true` to make every command and parameter write throw `ReadOnlyModeException`.

## Your own dialect

The package ships the generator. Add your dialect XML, plus the files it includes, as `AdditionalFiles`:

```xml
<PropertyGroup>
  <MavlinkNamespace>MyCompany.Drone.Mavlink</MavlinkNamespace>
  <MavlinkDialectName>Payload</MavlinkDialectName>
</PropertyGroup>
<ItemGroup>
  <AdditionalFiles Include="mavlink/payload.xml" />
  <AdditionalFiles Include="$(MavlinkDialectsPath)minimal.xml" />
</ItemGroup>
```

The generator produces message classes, enums and `PayloadDialect`. Combine it with the common dialect:
`o.Dialect = new CompositeDialect(PayloadDialect.Instance, CommonDialect.Instance)`. Invalid XML and missing includes
are reported as compiler errors (MAV001, MAV002).

## Signing

```csharp
var signing = MavlinkSigning.FromPassphrase("my-shared-secret", linkId: 1);   // SHA-256 of the passphrase
await using var link = MavlinkConnection.Create(o => { o.UseUdp(14550); o.Signing = signing; });
```

Outgoing frames are signed with a strictly increasing timestamp. Incoming frames with a bad signature, and unsigned
frames unless `AcceptUnsigned` is set, are dropped and counted in `link.Parser.SignatureErrors`.

## Simulator and tools

```bash
iotcom mavlink simulate --to 127.0.0.1:14550        # quadcopter over UDP (QGroundControl and Mission Planner can connect too)
iotcom mavlink listen --stats                       # live message rates and telemetry
iotcom mavlink cmd arm --allow-write                # arm, disarm, takeoff 15, land, rtl
iotcom mavlink params                               # read all; set with: params RTL_ALT 2000 --allow-write
iotcom mavlink decode FE09000101000000000002035104037DDD
dotnet run --project samples/console/MavlinkTelemetry
```

## Testing and interoperability

- **Shared vectors:** a third, independent Python implementation computes CRC_EXTRA for all 235 messages from the
  XML. It reproduces the published constants (HEARTBEAT 50, ATTITUDE 39, COMMAND_LONG 152, …). The generated C# and
  the Rust frame codec (`iotcom-mavlink`, fuzzed) both match it on v1 and v2 frames, including truncation, an all-zero
  payload and a signed frame.
- **End to end:** tests fly the simulator through the ground station: pre-arm denial, arm, takeoff, land and
  auto-disarm. They also download parameters over a link with 30% loss, check read-only mode, and run over real UDP
  and over a stream.
- **Generator:** a custom dialect is compiled in the test project itself.

## Limitations

Not yet included: the mission protocol helper (`MISSION_*` messages are available, the upload/download state machine
is not), MAVLink FTP, camera and gimbal microservices, and routing between several links.

## Learn more

Gallery demo *Drone telemetry over MAVLink* · notebook `notebooks/navigation/08-mavlink.en.ipynb` ·
[NMEA 0183](nmea.md) · `iotcom mavlink --help`
