---
title: CoAP
translation-status: synced
---

# CoAP

**Summary.** The Constrained Application Protocol (RFC 7252) gives battery-powered and microcontroller devices a
web-like interface over UDP: resources with paths, GET/PUT/POST/DELETE, response codes and content formats, in
datagrams of a few bytes. IoTCom.Net implements a client and a server with:

- confirmable messages, retransmission and deduplication;
- separate responses;
- **Observe** (RFC 7641), **Block-wise transfers** (RFC 7959) and **resource discovery** (RFC 6690);
- SenML payloads.

It also includes a simulated greenhouse node.

![Smart greenhouse over CoAP](../../images/gallery-coap.png)

## When to use it

- Talking to devices that speak CoAP: Zephyr, RIOT, Contiki-NG, OpenThread/Thread devices, LwM2M clients, and many
  NB-IoT and LTE-M modules.
- Building a device or gateway that offers a lightweight REST interface on constrained networks.
- Getting push updates (Observe) from sensors without polling or a broker.

## Roles

| Role | Type |
|---|---|
| Client / subscriber | `CoapClient`: `GetAsync`, `PutAsync`, `PostAsync`, `DeleteAsync`, `ObserveAsync` (`IAsyncEnumerable`), `DiscoverAsync`, `PingAsync` |
| Server | `CoapServer`: `Map(path, get, put, post, delete, observable…)`, `resource.NotifyAsync()` |
| Codec | `CoapMessage` (`Encode`/`TryDecode`), `CoapBlock`, `CoapLinkFormat`, `CoapAnatomy` (sans-I/O) |
| Simulator | `CoapDeviceSimulator`: greenhouse node with observable sensors, actuators, a Block2 log, Block1 firmware upload and a slow routine |

## Transports

CoAP runs on datagrams: `UseUdp(port)` (default 5683) or `UseInMemory(network)` on an `InMemoryDatagramNetwork`. The
in-memory network has `LossRate` and `DuplicateRate` settings for testing reliability. DTLS (coaps, port 5684) is
planned (Phase 2).

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Coap --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.Coap;

await using var coap = CoapClient.Create(o => o.UseServer("192.168.1.40"));
await coap.ConnectAsync();

var temp = await coap.GetAsync("/sensors/temperature");
Console.WriteLine($"{temp.Code}: {temp.PayloadText}");          // 2.05 Content: 27.5

await coap.PutAsync("/actuators/fan", "on");                      // 2.04 Changed

await foreach (var n in coap.ObserveAsync("/sensors/temperature"))
    Console.WriteLine($"{n.PayloadText} (seq {n.ObserveSequence})");
```

## Serving resources

```csharp
await using var server = CoapServer.Create(o => o.UseUdp(5683));
double level = 41;
var soil = server.Map("/sensors/soil",
    get: (req, ct) => ValueTask.FromResult(CoapReply.Content(level.ToString("0.0"))),
    observable: true, resourceType: "soil-moisture");
server.Map("/actuators/valve",
    put: (req, ct) => { /* req.PayloadText */ return ValueTask.FromResult(CoapReply.Changed()); });
await server.StartAsync();

level = 43.5;
await soil.NotifyAsync();     // pushes the new value to every observer
```

The server handles the protocol details for you:

- `/.well-known/core` is generated from the registered resources, with filtering such as `?rt=temperature*`.
- Unknown critical options are rejected with 4.02, and unsupported methods with 4.05.
- A handler slower than `SeparateResponseAfter` (800 ms) is acknowledged first and answered later as a separate
  response.
- Large bodies are served in Block2 blocks, and Block1 uploads are reassembled before the handler runs.

## How reliability works

| Mechanism | Behaviour |
|---|---|
| Confirmable (CON) | retransmitted after `AckTimeout × [1, 1.5]`, doubling, up to `MaxRetransmit` (4) times |
| Non-confirmable (NON) | sent once (requests set `Confirmable = false`; most notifications) |
| Deduplication | received message IDs are remembered for `DeduplicationLifetime` (247 s); duplicates get the cached reply |
| Separate responses | an empty ACK first, then a CON response that the client acknowledges |
| Tokens | match responses and notifications to requests; an unknown token is answered with RST, which ends an observation |
| Observe | the server sends NON notifications, every 5th one CON so that dead observers are detected; the client drops stale notifications (RFC 7641 §3.4) and deregisters when you stop enumerating |

`client.Statistics` and `server.Statistics` count retransmissions, duplicates, resets and timeouts.

## Configuration

| Option | Default | Description |
|---|---|---|
| `Transmission.AckTimeout` | 2 s | ACK_TIMEOUT |
| `Transmission.MaxRetransmit` | 4 | MAX_RETRANSMIT |
| `Transmission.BlockSize` | 1024 | preferred block size (16–1024) for Block1/Block2 |
| `Transmission.ResponseTimeout` | 30 s | wait for separate or NON responses |
| `Confirmable` (client) | true | CON or NON requests |
| `ReadOnly` (client) | false | blocks PUT, POST and DELETE |
| `SeparateResponseAfter` (server) | 800 ms | when to switch to a separate response |

## Content formats

`CoapContentFormat` lists the registered formats: text/plain (0), link-format (40), JSON (50), CBOR (60),
SenML JSON (110) and SenML CBOR (112). The simulator answers its sensors in SenML when asked; parse the payload with
`SenMLCodec`:

```csharp
var r = await coap.GetAsync("/sensors/humidity", accept: CoapContentFormat.SenMLJson);
var record = SenMLCodec.Resolve(SenMLCodec.ParseJson(r.Payload.Span)).Single();
```

## Tools

```bash
iotcom coap serve --port 5683 --frames                      # simulated greenhouse node
iotcom coap discover coap://127.0.0.1/ --query "rt=temperature*"
iotcom coap get coap://127.0.0.1/sensors/temperature --accept senml
iotcom coap observe coap://127.0.0.1/sensors/soil
iotcom coap put coap://127.0.0.1/actuators/fan on --allow-write
iotcom coap ping coap://127.0.0.1/
dotnet run --project samples/console/CoapObserve
```

## Testing and interoperability

- **Codec:** the C# codec and the Rust crate `iotcom-coap` run the same vectors (`/conformance/coap.json`). They are
  produced by a third, independent encoder and include the RFC 7252 examples and malformed messages. The Rust codec
  is fuzzed (`cargo fuzz run coap`).
- **Endpoints:** tests cover GET/PUT, discovery filters, SenML, Block2 downloads and Block1 uploads, separate
  responses, Observe with deregistration, read-only mode, real UDP, and a link with 20–30% loss plus duplication,
  where every request must reach the handler exactly once.

## Security

Plain CoAP is unauthenticated and unencrypted. Use it on isolated networks or behind a VPN until DTLS/OSCORE support
arrives. Treat requests as untrusted input, and keep the client read-only when you only monitor.

## Limitations

Not yet included: DTLS (coaps), OSCORE, CoAP over TCP/WebSockets (RFC 8323), multicast requests, proxying, ETag
validation and If-Match handling in the server, and FETCH/PATCH (RFC 8132). LwM2M on top of this stack is planned for
Phase 2.

## Learn more

Gallery demo *Smart greenhouse over CoAP* · notebook `notebooks/messaging/07-coap.en.ipynb` · [SenML](senml.md) ·
`iotcom coap --help`
