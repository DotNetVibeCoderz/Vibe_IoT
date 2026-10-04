---
title: Simulator
translation-status: synced
---

# Simulator

Setiap protokol menyediakan cara untuk berjalan tanpa perangkat keras. Gunakan simulator untuk mengembangkan HMI,
menulis uji integrasi, menjalankan notebook, dan mendemokan Galeri.

| Protokol | Simulator | Jalankan |
|---|---|---|
| Modbus | `ModbusServer` + `ModbusSimulator.CreateVirtualPlc()` | `iotcom modbus serve --simulate` |
| NMEA 0183 | `NmeaServer` + `NmeaSimulator` | `iotcom nmea simulate` |
| Art-Net / sACN | dua node di loopback | `samples/console/ArtNetPlayer --simulate` |
| MQTT | `MqttBroker` | `iotcom mqtt broker` |

## Link dalam proses

`InMemoryTransportListener` menghubungkan client ke server di dalam satu proses — tanpa port, tanpa dialog firewall,
sepenuhnya deterministik:

```csharp
var link = new InMemoryTransportListener();
await using var device = ModbusServer.Create(o => o.ListenInMemory(link));
await using var client = ModbusClient.Create(o => o.UseInMemory(link));
```

## Waktu deterministik

Simulator dapat dijalankan manual, sehingga pengujian dapat diulang:

```csharp
var sim = ModbusSimulator.CreateVirtualPlc(store);
for (var i = 0; i < 40; i++) sim.Tick(0.25);       // 10 detik simulasi, seketika
Assert.True(store.InputRegisters[3] > 1000);       // motor mencapai kecepatan

var gps = new NmeaSimulator(seed: 7);
var epoch = gps.GenerateEpoch(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(1));
```

## Perilaku kustom

```csharp
sim.AddSignal(ModbusTable.InputRegisters, 100, t => 50 + 10 * Math.Sin(t / 5), ModbusValueType.Float32);
sim.AddBit(ModbusTable.DiscreteInputs, 10, t => (int)t % 20 < 10);
sim.AddBehavior((t, dt) => { if (store.Coils[5]) store.HoldingRegisters[5]++; });   // logika PLC
```
