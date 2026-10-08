---
title: Sparkplug B
translation-status: synced
---

# Sparkplug B (state-aware MQTT for industry)

**Summary.** Plain MQTT says nothing about what a topic contains or whether its publisher is still alive. Sparkplug B
(Eclipse Foundation) fixes both: a fixed topic namespace `spBv1.0/{group}/{type}/{edge node}[/{device}]`, Protobuf
payloads with typed metrics, and a session model built on MQTT's will message. IoTCom.Net implements it on top of the
MQTTnet adapter:

- `SparkplugPayload` / `SparkplugMetric` — the Protobuf payload (Tahu field numbers) for every scalar type, with
  `Describe` for the frame lane; `SparkplugTopic` parses and builds topics, including `spBv1.0/STATE/{host}`;
- `SparkplugEdgeNode` — registers NDEATH (with `bdSeq`) as its MQTT will, publishes NBIRTH and one DBIRTH per device
  with every metric and an alias, reports changes as NDATA/DDATA with a sequence number 0–255, answers
  `Node Control/Rebirth`, applies writes to writable metrics from NCMD/DCMD (or ignores them with `AcceptWrites = false`),
  and sends DDEATH/NDEATH when stopped;
- `SparkplugHost` — a host application: STATE online (retained, with an offline will), follows births and deaths,
  resolves aliases, detects sequence gaps and data from nodes it has not seen born, asks for a rebirth once, and sends
  typed writes using the birth certificate;
- `SparkplugLineSimulator` — a bottling line (filler and syrup tank) for demos and tests.

## When to use it

- Publishing plant data to Ignition, HiveMQ, EMQX, Cirrus Link or any Unified Namespace that speaks Sparkplug.
- Building a lightweight SCADA-side consumer that needs to know which values are current and which are stale.
- Testing an edge gateway against a host without the real SCADA system.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Sparkplug --prerelease   # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.Sparkplug;

await using var node = SparkplugEdgeNode.Create(o =>
{
    o.Group = "Plant"; o.EdgeNode = "Line1";
    o.Mqtt = m => m.UseBroker("broker.local").WithCredentials("edge", "secret");
});
node.Device("Filler")
    .Metric("Speed", SparkplugDataType.Float, 0f)
    .Metric("Running", SparkplugDataType.Boolean, true, writable: true);
node.CommandReceived += (_, c) => Console.WriteLine($"write {c.Device}/{c.Metric} = {c.Value}");
await node.StartAsync();                                       // NBIRTH, DBIRTH
await node.Devices["Filler"].SetAsync("Speed", 118.5f);         // DDATA, by alias

await using var host = SparkplugHost.Create(o => { o.HostId = "scada"; o.Mqtt = m => m.UseBroker("broker.local"); });
host.MetricUpdated += (_, e) => Console.WriteLine($"{e.View.Key} {e.Metric}");
host.StateChanged += (_, v) => Console.WriteLine($"{v.Key} {(v.Online ? "online" : "offline")}");
await host.StartAsync();
await host.WriteAsync("Plant", "Line1", "Filler", "Running", false);   // DCMD
```

With hosting: `services.AddIoTCom(b => b.AddSparkplugEdgeNode(o => …, node => node.Device("Filler").Metric(…)))` and
`AddSparkplugHost(o => …)` register singletons that start and stop with the application.

## Safety

Writes change running equipment. Only metrics declared `writable` accept NCMD/DCMD; `AcceptWrites = false` makes an
edge node ignore every write while still honouring rebirth requests. The CLI requires `--allow-write` and a
confirmation for `iotcom sparkplug write`.

## Tools

```bash
iotcom sparkplug watch --sim                     # embedded broker + simulated line, live message log
iotcom sparkplug watch --host broker.local       # follow a real namespace as host "iotcom-cli"
iotcom sparkplug simulate --embedded-broker      # an edge node others can watch
iotcom sparkplug write Plant/Line1/Filler Running false --allow-write
iotcom payload sparkplug <hex>                   # decode a captured payload
```

Gallery: *Plant network · Sparkplug B* (pull the network cable and watch the NDEATH will arrive). The VS Code
extension decodes `sparkplug` payloads in the frame viewer.

## Testing

Payload tests check bytes against a hand-encoded Tahu payload and round-trip every scalar type, negative integers and
nulls; topic tests cover all message types and STATE. Session tests run against the embedded broker: births, data by
alias, DCMD writes, non-writable metrics, the NDEATH will after a dropped connection with `bdSeq`, a host that joins
late and asks for a rebirth, and a read-only edge node.

## Limitations

Data sets, templates, metric properties and metadata are skipped when decoding and not produced when encoding.
Historical data buffering while offline is not included. Sparkplug 3.0 host STATE (JSON) is used; the 2.2 plain-text
STATE is not.

## Learn more

Notebook `notebooks/messaging/12-mdns-sparkplug.en.ipynb` · sample `samples/console/SparkplugEdgeNode` · [MQTT](mqtt.md)
