---
title: Zenoh
translation-status: synced
---

# Zenoh

**Summary.** Eclipse Zenoh joins publish/subscribe, storage-style queries and computation across devices, gateways and
the cloud. Data lives under hierarchical key expressions such as `plant/line1/temp`; a subscriber asks for
`plant/*/temp` or `plant/**`, a queryable answers `get` requests, and peers find each other by multicast scouting or
through a router. `IoTCom.Net.Adapters.Zenoh` is an adapter over the Rust `zenoh` crate (not a rewrite), through the native library
`iotcom_zenoh`; in this release only its in-process network ships (see *Native library* below). It provides:

- `ZenohSession`:
  - `PutAsync` and `DeleteAsync` on concrete keys (text or bytes, with an optional encoding)
  - `Subscribe` (a handler you dispose), the `SampleReceived` event and `WatchAsync` (`IAsyncEnumerable<ZenohSample>`)
    for key expressions with `*` and `**`
  - `DeclareQueryable` (`ZenohQuery` with key, parameters and payload, `ReplyAsync`, `ReplyErrorAsync`) and `GetAsync`
    (collects the replies of every matching queryable, with a timeout)
  - the shared `IPublisher<T>` / `ISubscriber<T>` abstractions for `byte[]` and `string`; deletes are skipped there
  - peer or client mode, `Connect`/`Listen` endpoints (`tcp/host:port`, `udp/host:port`), multicast scouting on or off,
    a `ReadOnly` option, state changes and the traffic tap
- `ZenohKeyExpr`: pure C# validation and matching (`IsValid`, `Includes`, `Intersects`, `IsWild`) following the Zenoh
  rules for `*`, `**` and the empty chunk.
- `VirtualZenohNetwork`: an in-process network for tests, notebooks and `--sim`; it needs no native library.
- `NativeZenohBackend`: the binding to zenoh 1.10.1 (TCP and UDP transports only); it needs `iotcom_zenoh`, which this
  release does not ship.

## When to use it

- Moving data between robots, edge gateways and servers where brokers are awkward and peers should find each other.
- Asking a device for its current state (`get`) instead of waiting for it to publish.
- Watching a whole plant subtree with one wildcard subscription.
- Running the same code against `VirtualZenohNetwork` in tests and against real peers in the field.

## Installation

```bash
dotnet add package IoTCom.Net.Adapters.Zenoh --prerelease    # not part of the IoTCom.Net meta-package
```

### Native library

The package does **not** ship `iotcom_zenoh` yet. zenoh 1.10.1 compiles in `lz4_flex` 0.10, which has security advisory
RUSTSEC-2026-0041 (decompressing invalid LZ4 data can leak uninitialised memory) and no compatible fix. The binding
returns once zenoh moves to a fixed `lz4_flex`. Until then, `UseVirtual(...)` works everywhere; to talk to a real Zenoh
network you can build `rust/crates/native/iotcom-zenoh-native` yourself (it is outside the Rust workspace) and point
`IOTCOM_NATIVE_PATH` at it, accepting that advisory.

## Quickstart

```csharp
using IoTCom.Net.Adapters.Zenoh;

await using var zenoh = ZenohSession.Create(o => o.Connect("tcp/192.168.1.10:7447"));   // a router or another peer
await zenoh.ConnectAsync();
await zenoh.PutAsync("plant/line1/temp", "21.5");

await foreach (var sample in zenoh.WatchAsync("plant/**", ct))
    Console.WriteLine($"{sample.Key} = {sample.Text}");
```

Asking for state, and answering:

```csharp
using var info = zenoh.DeclareQueryable("plant/line1/info", async q =>
    await q.ReplyAsync("plant/line1/info", "{\"state\":\"running\"}"));

foreach (var reply in await zenoh.GetAsync("plant/*/info"))
    Console.WriteLine(reply.IsError ? reply.ErrorText : reply.Sample!.Text);
```

Without any network:

```csharp
var net = new VirtualZenohNetwork();
await using var a = ZenohSession.Create(o => o.UseVirtual(net));
await using var b = ZenohSession.Create(o => o.UseVirtual(net));
```

## Safety

Publishing changes what every matching subscriber sees, which on a plant network may be setpoints. `PutAsync`,
`DeleteAsync` and query replies are on by default because this is a pub/sub adapter; set `ReadOnly = true` and they
throw `ReadOnlyModeException` while subscribing and `get` keep working. The CLI is read-only for `sub` and `get` and
requires `--allow-write` plus a confirmation for `pub`. Zenoh multicast scouting joins any session on the same network
segment; switch it off (`MulticastScouting = false`) and name the endpoints explicitly when you want a closed topology.
This build has no TLS or authentication transports.

## Tools

```bash
iotcom zenoh sub "plant/**" --sim -n 6                                  # simulated plant on an in-process network
iotcom zenoh sub "plant/**" --connect tcp/192.168.1.10:7447 --no-scouting
iotcom zenoh get "plant/*/info" --sim
iotcom zenoh pub plant/line1/setpoint 42 --sim --allow-write
```

`--mode peer|client`, `--connect`, `--listen` and `--no-scouting` open a real session; `--sim` runs an in-process
network with a small plant that publishes `plant/<line>/temp` and `plant/<line>/pressure` and answers `plant/*/info`.

## Testing

Key-expression tests cover:
- `Includes` and `Intersects` for `*`, `**`, `$*` and mixed wildcards, including symmetry between the two sides;
- validation of malformed keys, and wildcard detection.

Virtual-network session tests cover:
- a put reaching matching subscribers on other sessions only, with the right encoding, and a session also receiving its own
  publications and the `SampleReceived` event;
- delete delivered as a delete sample, and no more samples after the subscription is disposed;
- `WatchAsync` streaming until cancelled, and the shared publisher/subscriber abstractions skipping deletes;
- `GetAsync` collecting replies from every matching queryable with parameters and body, an error reply, no queryables,
  a queryable that forgets to answer or throws, a reply key that must intersect the query, and undeclared queryables;
- `ReadOnly` refusing put, delete and reply but allowing subscribe and get;
- invalid keys, closed sessions, state changes and the traffic tap.

Three native tests run when the `iotcom_zenoh` library is built (`cargo build --release -p iotcom-zenoh-native`) and
skip otherwise: two native sessions exchanging put, delete and a query over TCP (and an empty `get` when nobody answers), the documented defaults and mode
validation, and a bad endpoint reported as a transport error.

## Limitations

The native build has the TCP and UDP transports only: no TLS, QUIC, WebSocket, serial or shared-memory transport, and
no access control. Storages, liveliness and attachments are not exposed, and there are no declared publishers (each put is sent
directly). It is a client of a Zenoh network, not a router: run `zenohd` when you
need one. Not part of the meta-package.

## Learn more

Notebook `notebooks/messaging/22-zenoh.en.ipynb` · [Messaging brokers](messaging-brokers.md) · [Endpoints and transports](../concepts/endpoints-and-transports.md)
