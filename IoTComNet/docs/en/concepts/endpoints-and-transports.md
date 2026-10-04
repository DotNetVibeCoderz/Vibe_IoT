---
title: Endpoints and transports
translation-status: synced
---

# Endpoints and transports

## Roles

| Interface | Role | Implemented by |
|---|---|---|
| `IClientEndpoint` | connects to a peer (master, tester, reader) | `ModbusClient`, `NativeModbusClient`, `NmeaReader`, `MqttEndpoint` |
| `IServerEndpoint` | serves peers (slave, device, simulator) | `ModbusServer`, `NmeaServer`, `ArtNetNode`, `SacnNode`, `MqttBroker` |
| `IPublisher<T>` | publishes to topics | `MqttEndpoint`, `NmeaServer`, DMX nodes |
| `ISubscriber<T>` | `IAsyncEnumerable` subscriptions | `MqttEndpoint`, `NmeaReader`, DMX nodes |

Request/response protocols (Modbus) expose their own typed API (`ReadHoldingRegistersAsync`, …) and only implement
the client/server role. Pub/sub-shaped protocols implement the shared publisher/subscriber contracts.

## Lifecycle

Every endpoint has a `State` (`Disconnected → Connecting → Connected/Listening → Stopping`, or `Faulted`) and raises
`StateChanged`. Endpoints are `IAsyncDisposable`: use `await using`.

Clients connect lazily — the first request connects if you have not called `ConnectAsync`. After a link loss, the
next request reconnects following the `ReconnectPolicy` (exponential backoff with jitter; `ReconnectPolicy.None`
disables it).

## Builders and transports

Every endpoint is created with a fluent builder. Transport extensions are shared by all protocols:

```csharp
ModbusClient.Create(o => o.UseTcp("10.0.0.5", 502));          // Core
ModbusClient.Create(o => o.UseSerial("/dev/ttyUSB0", 19200));  // Transport.Serial
ModbusClient.Create(o => o.UseInMemory(listener));             // Core: tests, simulators

ModbusServer.Create(o => o.UseTcp(IPAddress.Any, 1502));       // listener
ModbusServer.Create(o => o.ServeSerial("COM5", 9600));         // a slave on RS-485
ModbusServer.Create(o => o.ListenInMemory(listener));
```

`ITransport` exposes an `IDuplexPipe` (`System.IO.Pipelines`): protocol code reads `ReadOnlySequence<byte>` without
copying and writes through `PipeWriter`. To support a new link (TLS, WebSocket, a USB-CDC quirk), implement
`ITransport` — or derive from `StreamTransport` and return a `Stream` — and call `UseTransport(() => new MyTransport())`.

`InMemoryTransportListener` connects a client and a server inside one process. The Gallery, the notebooks and most
tests use it, which is why nothing needs hardware.

## Error model

| Exception | Meaning |
|---|---|
| `IoTComException` | base type |
| `TransportException` | the link failed (refused, unplugged, closed) |
| `ProtocolException` | malformed bytes, bad checksum, unexpected response |
| `IoTComTimeoutException` | no answer in time |
| `DeviceException` (`ModbusException`) | the device answered with an error; the vendor code is preserved (`ExceptionCode`) |
| `ReadOnlyModeException` | a write was attempted on a read-only endpoint |

Every operation accepts a `CancellationToken`; there are no hidden blocking calls.

## Threading

Endpoints are safe to call concurrently. Modbus TCP pipelines requests by transaction id (up to
`MaxConcurrentRequests`); RTU and ASCII serialise them as the bus requires. Events (`MessageReceived`,
`RequestHandled`, `DmxReceived`, …) are raised on I/O threads — marshal to your UI thread before touching controls.
