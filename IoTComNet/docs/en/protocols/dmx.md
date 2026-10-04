---
title: Art-Net 4 and sACN (DMX512 over IP)
translation-status: synced
---

# Art-Net 4 and sACN (DMX512 over IP)

**Summary.** DMX512 drives stage, architectural and effect lighting: 512 one-byte channels per *universe*. Art-Net and
sACN (ANSI E1.31) carry universes over UDP. IoTCom.Net sends and receives both, discovers Art-Net nodes, and models a
universe with fades.

## When to use it

- Controlling LED fixtures, dimmers, pixel strips and moving lights from .NET.
- Building a lighting console, a visualiser, or a bridge from sensors/MQTT to light.
- Monitoring what a console is sending.

## Roles

| Role | Type |
|---|---|
| Sender / receiver / discovery | `ArtNetNode` — ArtDmx, ArtPoll/ArtPollReply, ArtSync |
| Sender / receiver | `SacnNode` — multicast per universe, priority, E1.31 sequence rules |
| Universe model | `DmxUniverse` — 1-based channels, snapshots, crossfades |
| Codecs | `ArtNetPacket`, `SacnPacket` |

## Transports

UDP only: Art-Net port 6454 (broadcast or unicast), sACN port 5568 (multicast `239.255.{hi}.{lo}` or unicast).
`Bind(address, port)` selects the local interface; `SendTo(endpoint)` forces unicast.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Dmx --prerelease
```

## Quickstart

```csharp
await using var node = ArtNetNode.Create(o => o.WithName("Stage left"));
await node.StartAsync();
await node.SendDmxAsync(universe: 0, new byte[] { 255, 120, 0 });   // fixture 1: amber
```

```csharp
await using var sacn = SacnNode.Create(o => o.WithPriority(150));
await sacn.StartAsync();
sacn.JoinUniverse(1);
await foreach (var frame in sacn.ReceiveAsync(universe: 1)) Console.WriteLine(frame.Data.Span[0]);
```

## Configuration

| Option | Applies to | Description |
|---|---|---|
| `Bind(address, port)` | both | local interface/port (port 0 = ephemeral) |
| `SendTo(endpoint)` | both | unicast destination |
| `WithName(name)` | both | Art-Net short name / sACN source name |
| `RespondToPoll` | Art-Net | answer ArtPoll (default true) |
| `WithPriority(0–200)` | sACN | source priority (default 100) |
| `Cid` | sACN | stable component id |

## Examples

```csharp
// Discover nodes
await node.PollAsync();
await Task.Delay(2000);
foreach (var n in node.Nodes) Console.WriteLine($"{n.ShortName} @ {n.Address}");

// Port-address = Net(7) | SubNet(4) | Universe(4)
int universe = ArtNetPacket.PortAddress(net: 0, subNet: 1, universe: 2);

// Smooth fade
var u = new DmxUniverse(0);
var from = u.Snapshot();
for (var p = 0.0; p <= 1; p += 0.02) { u.Crossfade(from, target, p); await node.SendDmxAsync(0, u.Snapshot()); await Task.Delay(25); }
```

Consoles resend every universe continuously (typically 25–44 fps) so receivers recover from lost packets — do the
same, as the Gallery demo and the `ArtNetPlayer` sample do.

## Testing and interoperability

Codec round-trips (port-address, even-length padding, sACN layer lengths for 512 slots, E1.31 sequence rules) and
live loopback exchanges between two nodes, including ArtPoll discovery.

## Security

Neither protocol authenticates senders. Keep lighting networks separate from office networks; on shared networks
prefer sACN with unicast and per-universe priority.

## Limitations

Art-Net RDM, ArtAddress/ArtIpProg and sACN universe discovery / synchronisation packets are not implemented yet.

## Learn more

Gallery demo *Stage lighting over Art-Net* · sample `samples/console/ArtNetPlayer` · `iotcom artnet --help`
