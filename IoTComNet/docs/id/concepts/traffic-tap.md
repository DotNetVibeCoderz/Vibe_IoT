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

## Merekam ke pcapng (Wireshark)

`PcapngTap` menulis setiap frame yang dilihat tap ke berkas pcapng. Wireshark, tshark, dan tcpdump bisa membukanya,
dan Wireshark mengurai protokolnya dengan decoder bawaannya sendiri:

| Frame | Ditulis sebagai | Yang ditampilkan Wireshark |
|---|---|---|
| CAN / CAN FD (`can`, `can-slcan`) | `LINKTYPE_CAN_SOCKETCAN` | CAN, serta ISO-TP/UDS setelah decoder-nya diaktifkan |
| Modbus/TCP, NMEA, HL7 | IPv4 + TCP di 502, 10110, 2575 | Modbus/TCP dengan kode fungsi; teks untuk NMEA dan HL7 |
| CoAP, Art-Net, sACN, MAVLink | IPv4 + UDP di 5683, 6454, 5568, 14550 | CoAP, Art-Net, sACN (MAVLink butuh plugin komunitas) |
| lainnya (UDS, publish MQTT, …) | `LINKTYPE_USER0` | byte mentah, dengan protokol dan ringkasan terurai sebagai komentar paket |

Paket IP sintetis memakai 10.0.0.1 (kita) dan 10.0.0.2 (peer), nomor urut TCP yang konsisten, dan checksum yang valid.
Arah dicatat di `epb_flags`. CI menjalankan `tshark` pada capture yang dihasilkan pengujian, sehingga enkapsulasinya
diperiksa langsung terhadap Wireshark.

```csharp
using var pcap = PcapngTap.Create("plc.pcapng");   // di-flush per paket: aman dihentikan kapan saja
await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithTap(pcap));
```

Tab Traffic di Galeri punya tombol **Simpan .pcapng**, dan CLI menulis capture dengan `--pcap`:

```bash
iotcom sniff tcp --listen 1502 --target 192.168.1.10:502 --pcap plc.pcapng    # proxy Modbus/TCP transparan
iotcom sniff udp --listen 15683 --target 192.168.1.40:5683 --protocol coap --lanes
iotcom sniff can --can socketcan:can0 --pcap bus.pcapng
```

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
