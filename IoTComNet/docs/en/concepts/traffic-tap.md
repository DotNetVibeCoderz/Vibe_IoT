---
title: Traffic tap and observability
translation-status: synced
---

# Traffic tap and observability

## The traffic tap

Every endpoint can report each raw frame it sends or receives:

```csharp
var tap = new RecordingTap(capacity: 1000);
await using var plc = ModbusClient.Create(o => o.UseTcp("10.0.0.5", 502).WithTap(tap));
await plc.ReadHoldingRegistersAsync(0, 10);

foreach (var frame in tap.Snapshot())
    Console.WriteLine($"{frame.Direction} {HexDump.ToHex(frame.Data.Span)}  {frame.Summary}");
// Outbound 00 01 00 00 00 06 01 03 00 00 00 0A  unit=1 ReadHoldingRegisters addr=0 count=10
```

| Type | Use |
|---|---|
| `ITrafficTap` | implement `OnFrame(in TrafficFrame)` for your own sink (file, pcap, websocket) |
| `RecordingTap` | bounded ring buffer + `FrameCaptured` event (UIs, diagnostics) |
| `DelegateTap` | adapt a lambda |
| `TrafficTapHub` | fan-out; every `EndpointBase` has one (`AddTap` returns an `IDisposable` to detach) |

Taps only cost anything while attached. A failing tap never breaks the I/O path.

## The frame lane

`ModbusAnatomy.Describe(frame, mode, isRequest)` splits a frame into named `FrameField`s with a `FrameFieldKind`
(header, address, function, length, data, checksum, delimiter, error). The CLI, the Gateway dashboard and the Gallery
render the same data as a *frame lane* — byte tiles coloured by field:

![Frame lane in the Gallery](../../images/gallery-traffic.png)

```bash
iotcom modbus decode "11 03 00 6B 00 03 76 87" --mode rtu
```

## Metrics and traces

IoTCom.Net publishes an `ActivitySource` and a `Meter`, both named `IoTCom.Net`:

| Instrument | Unit | Tags |
|---|---|---|
| `iotcom.frames.in` / `iotcom.frames.out` | frames | `protocol` |
| `iotcom.bytes.in` / `iotcom.bytes.out` | bytes | `protocol` |
| `iotcom.errors` | errors | `protocol` |
| `iotcom.reconnects` | attempts | `protocol` |
| `iotcom.request.duration` | ms (histogram) | `protocol` |

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("IoTCom.Net"))
    .WithTracing(t => t.AddSource("IoTCom.Net"));
```

Modbus requests create `modbus <Function>` client activities tagged with the unit id.

## Logging

Pass an `ILogger` with `.WithLogger(...)` or let [hosting](../guides/hosting.md) inject one per endpoint. Credentials
are never logged.
