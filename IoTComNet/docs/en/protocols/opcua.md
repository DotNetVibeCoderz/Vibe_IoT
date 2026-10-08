---
title: OPC UA
translation-status: synced
---

# OPC UA (adapter over the OPC Foundation stack)

**Summary.** OPC UA (IEC 62541) is how PLCs, SCADA, MES and historians publish their data: an address space of
objects, variables and methods, reached over a session with application certificates, encryption and user identity.
Following the design's curation rule, IoTCom.Net does **not** reimplement OPC UA. `IoTCom.Net.Adapters.OpcUa` wraps
the OPC Foundation .NET Standard stack (`OPCFoundation.NetStandard.Opc.Ua.Client`/`.Server` 1.5.378, MIT) and adds:

- `OpcUaClient` — an IoTCom client endpoint: connect (most secure endpoint by default, or SecurityPolicy None;
  anonymous or user name), `BrowseAsync`, `ReadAsync` (one or many), `WriteAsync` (value converted to the variable's
  type), `CallAsync`, and `SubscribeAsync` returning `IAsyncEnumerable<OpcUaValue>`; a `ReadOnly` switch that refuses
  writes and method calls; service calls on the traffic tap;
- `OpcUaPlantServer` — a simulator with a bottling line (filler, syrup tank, high-level alarm, `ResetCounter` method)
  offering None and Basic256Sha256 Sign / SignAndEncrypt, for tests, demos and client development.

## When to use it

- Reading and writing tags of a PLC or a SCADA server from .NET services, gateways and tests.
- Bridging OPC UA to MQTT / Sparkplug B or a database with the rest of IoTCom.Net.
- Developing an OPC UA client without plant equipment, against the simulator.

## Installation

```bash
dotnet add package IoTCom.Net.Adapters.OpcUa --prerelease
```

The adapter is not part of the IoTCom.Net meta-package: the OPC Foundation stack is large and not trimming/AOT safe.

## Quickstart

```csharp
using IoTCom.Net.Adapters.OpcUa;

await using var ua = OpcUaClient.Create(o =>
{
    o.UseEndpoint("opc.tcp://plc.local:4840");
    o.WithCredentials("operator", Environment.GetEnvironmentVariable("PLC_PASSWORD")!);
    o.ReadOnly = true;                                       // nothing below can change the device
});
await ua.ConnectAsync();                                     // Basic256Sha256 / SignAndEncrypt when offered

foreach (var node in await ua.BrowseAsync()) Console.WriteLine(node);              // children of Objects
var speed = await ua.ReadAsync("ns=2;s=Plant/Line1/Filler/Speed");
Console.WriteLine($"{speed.Text} {speed.Status} {speed.SourceTimestamp}");

await foreach (var change in ua.SubscribeAsync(["ns=2;s=Plant/Line1/Tank7/Level"], TimeSpan.FromMilliseconds(500)))
    Console.WriteLine(change);
```

## Certificates and trust

Every OPC UA application has its own certificate. On first connect the client creates a self-signed one in its PKI
directory (`%LOCALAPPDATA%/IoTCom.Net/opcua/pki` by default, or `PkiPath`), with `own`, `trusted`, `issuer` and
`rejected` stores:

1. Connect once. If the server's certificate is unknown, the connection fails and the certificate is written to
   `rejected/`. Check it, then move it to `trusted/certs/`.
2. The server usually rejects the client's certificate the same way: trust it in the server's configuration tool.
3. `AcceptUntrustedCertificates = true` (CLI `--accept-untrusted`) skips step 1 — only for commissioning and tests.

## Safety

`ReadOnly = true` makes `WriteAsync` and `CallAsync` throw `ReadOnlyModeException` before anything reaches the
server. The CLI opens read-only sessions for `browse`, `read` and `watch`; `write` and `call` need `--allow-write`
(and a confirmation for `write` against a real server). Server-side access levels still apply: writing a read-only
variable fails with `DeviceException` (BadNotWritable).

## Tools

```bash
iotcom opcua simulate --port 4840                         # the plant simulator for other tools (UaExpert, Node-RED…)
iotcom opcua browse --sim                                 # in-process simulator, tree with values
iotcom opcua browse -e opc.tcp://plc.local:4840 --accept-untrusted
iotcom opcua read --sim Line1/Filler/Speed Line1/Tank7/Level
iotcom opcua watch --sim Line1/Tank7/Level -i 250
iotcom opcua write --sim Line1/Filler/Setpoint 100 --allow-write
iotcom opcua call --sim Line1 Line1/ResetCounter --allow-write
```

With `--sim`, node ids can be written relative to the plant (`Line1/Filler/Speed`). Gallery: *OPC UA tag browser*.

## Testing

Tests run the simulator and the client in-process: browsing the plant, reading values and unknown nodes
(BadNodeIdUnknown), writing with type conversion, refusing a write to a read-only variable, method calls, read-only
mode, subscriptions delivering changes, a Basic256Sha256 SignAndEncrypt session with certificates created on the fly,
and an unreachable endpoint reported as `TransportException`.

## Limitations

Events and alarms & conditions, historical access, complex types (structures) and the server's user authentication
are not wrapped yet; use `OpcUaClient.Session` for anything the adapter does not cover. The simulator accepts
anonymous users only.

## Learn more

Notebook `notebooks/industrial/13-opcua.en.ipynb` · sample `samples/console/OpcUaBrowser` · [Sparkplug B](sparkplug.md)
