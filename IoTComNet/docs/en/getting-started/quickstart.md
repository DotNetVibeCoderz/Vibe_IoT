---
title: Quickstart
translation-status: synced
---

# Quickstart

Read a PLC in fifteen lines — against a simulated one, so it runs anywhere.

```bash
dotnet new console -n FirstPlc && cd FirstPlc
dotnet add package IoTCom.Net --prerelease
```

```csharp
using IoTCom.Net;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transports;

// 1. A virtual device: a Modbus slave animated like a small production line.
var link = new InMemoryTransportListener();
var store = new ModbusDataStore();
await using var device = ModbusServer.Create(o => o.ListenInMemory(link).WithStore(store));
await using var simulator = ModbusSimulator.CreateVirtualPlc(store);
await device.StartAsync();
simulator.Start();

// 2. Your code: a Modbus master. For real hardware use .UseTcp("192.168.1.10", 502).
await using var plc = ModbusClient.Create(o => o.UseInMemory(link).WithUnitId(1));
var registers = await plc.ReadInputRegistersAsync(address: 0, count: 4);
Console.WriteLine($"temperature {registers[0] / 10.0} °C, motor {registers[3]} rpm");
```

## Point it at real equipment

| Device | Change |
|---|---|
| Modbus TCP PLC or gateway | `o.UseTcp("192.168.1.10", 502)` |
| Modbus RTU on RS-485 | `o.UseSerial("COM3", 19200, Parity.Even).UseRtuFraming()` (package `IoTCom.Net.Transport.Serial`) |
| RTU-over-TCP converter | `o.UseTcp("10.0.0.20", 4001).UseRtuFraming()` |

Add `.AsReadOnly()` while you explore an unknown installation: writes are then rejected before they reach the wire.

## Use the same pattern elsewhere

```csharp
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Transport.Serial;

// GPS on a serial port
await using var gps = NmeaReader.Create(o => o.UseSerial("COM4", 9600));
await gps.ConnectAsync();
await foreach (var fix in gps.ReadAsync<GgaMessage>()) Console.WriteLine($"{fix.Latitude}, {fix.Longitude}");

// MQTT
await using var mqtt = MqttEndpoint.Create(o => o.UseBroker("broker.local"));
await mqtt.PublishStringAsync("plant/line1/state", "running");
```

Next: [Architecture](../concepts/architecture.md) · [Modbus](../protocols/modbus.md) · [Gallery](../tools/gallery.md)
