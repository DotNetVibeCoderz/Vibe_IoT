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

## Capturing to pcapng (Wireshark)

`PcapngTap` writes every frame a tap sees to a pcapng file. Wireshark, tshark and tcpdump open it, and Wireshark
dissects the protocols with its own decoders:

| Frames | Written as | Wireshark shows |
|---|---|---|
| CAN / CAN FD (`can`, `can-slcan`) | `LINKTYPE_CAN_SOCKETCAN` | CAN, and ISO-TP/UDS once the decoders are enabled |
| Modbus/TCP, NMEA, HL7 | IPv4 + TCP on 502, 10110, 2575 | Modbus/TCP with function codes; text for NMEA and HL7 |
| CoAP, Art-Net, sACN, MAVLink | IPv4 + UDP on 5683, 6454, 5568, 14550 | CoAP, Art-Net, sACN (MAVLink needs the community plugin) |
| anything else (UDS, MQTT publishes, …) | `LINKTYPE_USER0` | the bytes, with the protocol and decoded summary as packet comment |

The synthetic IP packets use 10.0.0.1 (us) and 10.0.0.2 (the peer), consistent TCP sequence numbers and valid
checksums. Direction is recorded in `epb_flags`. CI runs `tshark` over a capture produced by the tests, so the
encapsulation is checked against Wireshark itself.

```csharp
using var pcap = PcapngTap.Create("plc.pcapng");   // flushed per packet: safe to stop at any time
await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithTap(pcap));
```

The Gallery's Traffic tab has **Save .pcapng**, and the CLI writes captures with `--pcap`:

```bash
iotcom sniff tcp --listen 1502 --target 192.168.1.10:502 --pcap plc.pcapng    # transparent Modbus/TCP proxy
iotcom sniff udp --listen 15683 --target 192.168.1.40:5683 --protocol coap --lanes
iotcom sniff can --can socketcan:can0 --pcap bus.pcapng
```

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
