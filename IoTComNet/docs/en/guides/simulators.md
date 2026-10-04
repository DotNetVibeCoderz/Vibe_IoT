---
title: Simulators
translation-status: synced
---

# Simulators

Every protocol ships a way to run without hardware. Use simulators to develop HMIs, write integration tests, run the
notebooks and demo the Gallery.

| Protocol | Simulator | Run it |
|---|---|---|
| Modbus | `ModbusServer` + `ModbusSimulator.CreateVirtualPlc()` | `iotcom modbus serve --simulate` |
| NMEA 0183 | `NmeaServer` + `NmeaSimulator` | `iotcom nmea simulate` |
| Art-Net / sACN | two nodes on loopback | `samples/console/ArtNetPlayer --simulate` |
| MQTT | `MqttBroker` | `iotcom mqtt broker` |

## In-process links

`InMemoryTransportListener` connects a client to a server inside one process — no ports, no firewall prompts, fully
deterministic:

```csharp
var link = new InMemoryTransportListener();
await using var device = ModbusServer.Create(o => o.ListenInMemory(link));
await using var client = ModbusClient.Create(o => o.UseInMemory(link));
```

## Deterministic time

Simulators can be driven manually, which makes tests repeatable:

```csharp
var sim = ModbusSimulator.CreateVirtualPlc(store);
for (var i = 0; i < 40; i++) sim.Tick(0.25);       // 10 simulated seconds, instantly
Assert.True(store.InputRegisters[3] > 1000);       // motor up to speed

var gps = new NmeaSimulator(seed: 7);
var epoch = gps.GenerateEpoch(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(1));
```

## Custom behaviour

```csharp
sim.AddSignal(ModbusTable.InputRegisters, 100, t => 50 + 10 * Math.Sin(t / 5), ModbusValueType.Float32);
sim.AddBit(ModbusTable.DiscreteInputs, 10, t => (int)t % 20 < 10);
sim.AddBehavior((t, dt) => { if (store.Coils[5]) store.HoldingRegisters[5]++; });   // PLC logic
```
