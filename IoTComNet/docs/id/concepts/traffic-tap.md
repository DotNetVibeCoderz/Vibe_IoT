---
title: Traffic tap dan observability
translation-status: synced
---

# Traffic tap dan observability

## Traffic tap

Setiap endpoint dapat melaporkan setiap frame mentah yang dikirim atau diterimanya:

```csharp
var tap = new RecordingTap(capacity: 1000);
await using var plc = ModbusClient.Create(o => o.UseTcp("10.0.0.5", 502).WithTap(tap));
await plc.ReadHoldingRegistersAsync(0, 10);

foreach (var frame in tap.Snapshot())
    Console.WriteLine($"{frame.Direction} {HexDump.ToHex(frame.Data.Span)}  {frame.Summary}");
// Outbound 00 01 00 00 00 06 01 03 00 00 00 0A  unit=1 ReadHoldingRegisters addr=0 count=10
```

| Tipe | Kegunaan |
|---|---|
| `ITrafficTap` | implementasikan `OnFrame(in TrafficFrame)` untuk tujuan sendiri (file, pcap, websocket) |
| `RecordingTap` | ring buffer terbatas + event `FrameCaptured` (UI, diagnostik) |
| `DelegateTap` | adaptasi lambda |
| `TrafficTapHub` | fan-out; setiap `EndpointBase` memilikinya (`AddTap` mengembalikan `IDisposable` untuk melepas) |

Tap hanya berbiaya saat terpasang. Tap yang gagal tidak pernah merusak jalur I/O.

## Frame lane

`ModbusAnatomy.Describe(frame, mode, isRequest)` memecah frame menjadi `FrameField` bernama dengan `FrameFieldKind`
(header, address, function, length, data, checksum, delimiter, error). CLI, dashboard Gateway, dan Galeri menampilkan
data yang sama sebagai *frame lane* — ubin byte yang diwarnai per field:

![Frame lane di Galeri](../../images/gallery-traffic.png)

```bash
iotcom modbus decode "11 03 00 6B 00 03 76 87" --mode rtu
```

## Metrik dan trace

IoTCom.Net menerbitkan `ActivitySource` dan `Meter`, keduanya bernama `IoTCom.Net`:

| Instrumen | Satuan | Tag |
|---|---|---|
| `iotcom.frames.in` / `iotcom.frames.out` | frame | `protocol` |
| `iotcom.bytes.in` / `iotcom.bytes.out` | byte | `protocol` |
| `iotcom.errors` | error | `protocol` |
| `iotcom.reconnects` | percobaan | `protocol` |
| `iotcom.request.duration` | ms (histogram) | `protocol` |

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("IoTCom.Net"))
    .WithTracing(t => t.AddSource("IoTCom.Net"));
```

Request Modbus membuat activity client `modbus <Function>` dengan tag unit id.

## Logging

Berikan `ILogger` dengan `.WithLogger(...)` atau biarkan [hosting](../guides/hosting.md) menyuntikkannya per endpoint.
Kredensial tidak pernah dicatat di log.
