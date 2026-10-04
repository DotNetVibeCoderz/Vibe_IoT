# IoTCom.Net

**A complete IoT communication protocol library for .NET 10 — with a Rust core where it matters.**
Built by Gravicode Studios, led by Kang Fadhil · *Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

One consistent model — client, server, publisher, subscriber — for industrial, navigation, lighting and messaging
protocols. Every protocol ships a simulator, so you can build and test without hardware.

```csharp
await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithUnitId(1));
ushort[] registers = await plc.ReadHoldingRegistersAsync(address: 0, count: 10);
```

| Package | Contents |
|---|---|
| `IoTCom.Net` | meta-package with everything below + hosting extensions |
| `IoTCom.Net.Protocols.Modbus` | Modbus TCP/RTU/ASCII master, slave, simulator |
| `IoTCom.Net.Native.Modbus` | Rust protocol engine for Modbus |
| `IoTCom.Net.Protocols.Nmea` | NMEA 0183 reader, server, GPS simulator |
| `IoTCom.Net.Protocols.Dmx` | Art-Net 4 and sACN (E1.31) |
| `IoTCom.Net.Adapters.Mqtt` | MQTT adapter over MQTTnet + embedded broker |
| `IoTCom.Net.Serialization.SenML` | SenML JSON/CBOR |
| `IoTCom.Net.Framing` | CRC catalogue, SLIP, COBS, HDLC |
| `IoTCom.Net.Transport.Serial` | serial transport |
| `IoTCom.Net.Hosting` | DI, hosted lifecycle, health checks |
| `IoTCom.Net.Core` / `.Abstractions` | transports, traffic tap, diagnostics, contracts |
| `IoTCom.Net.Cli` | `iotcom` command-line tool |
| `IoTCom.Net.Templates` | `dotnet new iotcom-console`, `iotcom-worker` |

Documentation in English and Bahasa Indonesia, the Gallery desktop app, samples and notebooks are in the source
repository. Licensed under Apache-2.0.
