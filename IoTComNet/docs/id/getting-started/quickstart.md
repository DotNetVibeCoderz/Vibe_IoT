---
title: Mulai cepat
translation-status: synced
---

# Mulai cepat

Membaca PLC dalam lima belas baris — PLC simulasi, jadi bisa dijalankan di mana saja.

```bash
dotnet new console -n FirstPlc && cd FirstPlc
dotnet add package IoTCom.Net --prerelease
```

```csharp
using IoTCom.Net;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transports;

// 1. Perangkat virtual: slave Modbus yang dianimasikan seperti lini produksi kecil.
var link = new InMemoryTransportListener();
var store = new ModbusDataStore();
await using var device = ModbusServer.Create(o => o.ListenInMemory(link).WithStore(store));
await using var simulator = ModbusSimulator.CreateVirtualPlc(store);
await device.StartAsync();
simulator.Start();

// 2. Kode Anda: master Modbus. Untuk perangkat sungguhan gunakan .UseTcp("192.168.1.10", 502).
await using var plc = ModbusClient.Create(o => o.UseInMemory(link).WithUnitId(1));
var registers = await plc.ReadInputRegistersAsync(address: 0, count: 4);
Console.WriteLine($"suhu {registers[0] / 10.0} °C, motor {registers[3]} rpm");
```

## Arahkan ke peralatan sungguhan

| Perangkat | Ubah |
|---|---|
| PLC atau gateway Modbus TCP | `o.UseTcp("192.168.1.10", 502)` |
| Modbus RTU di RS-485 | `o.UseSerial("COM3", 19200, Parity.Even).UseRtuFraming()` (paket `IoTCom.Net.Transport.Serial`) |
| Konverter RTU-over-TCP | `o.UseTcp("10.0.0.20", 4001).UseRtuFraming()` |

Tambahkan `.AsReadOnly()` saat menjelajahi instalasi yang belum dikenal: penulisan ditolak sebelum mencapai jalur.

## Pola yang sama untuk protokol lain

```csharp
using IoTCom.Net.Adapters.Mqtt;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Transport.Serial;

// GPS di port serial
await using var gps = NmeaReader.Create(o => o.UseSerial("COM4", 9600));
await gps.ConnectAsync();
await foreach (var fix in gps.ReadAsync<GgaMessage>()) Console.WriteLine($"{fix.Latitude}, {fix.Longitude}");

// MQTT
await using var mqtt = MqttEndpoint.Create(o => o.UseBroker("broker.local"));
await mqtt.PublishStringAsync("plant/line1/state", "running");
```

Berikutnya: [Arsitektur](../concepts/architecture.md) · [Modbus](../protocols/modbus.md) · [Galeri](../tools/gallery.md)
