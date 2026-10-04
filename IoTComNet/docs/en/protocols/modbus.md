---
title: Modbus TCP / RTU / ASCII
translation-status: synced
---

# Modbus TCP / RTU / ASCII

**Summary.** The request/response protocol of PLCs, energy meters, drives, I/O modules and sensors. IoTCom.Net gives you
the master (client) and the slave (server) in one API, a built-in simulator, and an optional Rust protocol engine.

## When to use it

- Reading or writing a PLC, meter, VFD, inverter or remote I/O on a LAN or an RS-485 bus.
- Simulating a device for HMI/SCADA development or automated tests.
- Building a gateway (Modbus → MQTT/HTTP) — see the [gateway sample](../guides/gateway.md).

## Roles

| Role | Type | Notes |
|---|---|---|
| Master / client | `ModbusClient` | TCP pipelining, RTU/ASCII serialisation, retries, reconnect, read-only mode |
| Master (Rust engine) | `NativeModbusClient` | same `IModbusClient` API, Rust sans-I/O state machine |
| Slave / server | `ModbusServer` | many TCP clients, unit-id filter, broadcasts, device identification |
| Simulator | `ModbusSimulator` | animates a `ModbusDataStore`; `CreateVirtualPlc()` ready-made map |

Functions: 0x01–0x06, 0x0F, 0x10, 0x16 (mask write), 0x17 (read/write), 0x2B/0x0E (device identification), raw PDUs via `SendAsync`.

## Transports

| Variant | Client | Server |
|---|---|---|
| Modbus TCP | `UseTcp(host, 502)` | `UseTcp(IPAddress.Any, 502)` |
| RTU on RS-485/232 | `UseSerial(port, baud, Parity.Even).UseRtuFraming()` | `ServeSerial(port, baud).UseRtuFraming()` |
| RTU over TCP | `UseTcp(host, port).UseRtuFraming()` | `UseTcp(...).UseRtuFraming()` |
| ASCII | `.UseAsciiFraming()` | `.UseAsciiFraming()` |
| In-process | `UseInMemory(listener)` | `ListenInMemory(listener)` |

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Modbus --prerelease
dotnet add package IoTCom.Net.Transport.Serial --prerelease   # for RTU on serial ports
dotnet add package IoTCom.Net.Native.Modbus --prerelease      # optional Rust engine
```

## Quickstart

```csharp
await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithUnitId(1));
ushort[] regs = await plc.ReadHoldingRegistersAsync(address: 0, count: 10);
float flow = await plc.ReadSingleAsync(100, ModbusWordOrder.WordSwap);
await plc.WriteSingleRegisterAsync(20, 1500);
```

## Configuration

| Option | Default | Description |
|---|---|---|
| `WithUnitId(byte)` | 1 | default unit id (override per call with `unitId:`) |
| `WithTimeout(TimeSpan)` | 1 s | response timeout |
| `WithRetries(int)` | 0 | retries after a timeout |
| `AsReadOnly()` | off | every write throws `ReadOnlyModeException` before reaching the wire |
| `WithMaxConcurrentRequests(int)` | 16 | in-flight requests on Modbus TCP (RTU/ASCII always 1) |
| `WithReconnect(ReconnectPolicy)` | backoff 0.5 s → 30 s | reconnect after link loss |
| `WithTap(ITrafficTap)` | — | capture frames |
| Server `WithUnitIds(...)` | all | answer only these units (TCP: others get exception 0x0B) |
| Server `WithStore(store)` | new store | share data with a simulator or your app |
| Server `AsReadOnly()` | off | writes answer IllegalFunction |

Addresses are **0-based** protocol addresses: documentation that says "40001" means holding register 0.

## Examples

```csharp
// Multi-register values: four word orders exist in the field.
var regs = await plc.ReadHoldingRegistersAsync(0, 4);
double energy = ModbusConvert.ToDouble(regs, ModbusWordOrder.BigEndian);

// Device identification (0x2B / 0x0E)
var id = await plc.ReadDeviceIdentificationAsync();

// A slave that reacts to writes
await using var slave = ModbusServer.Create(o => o.UseTcp(IPAddress.Any, 1502));
slave.Store.HoldingRegisters.Changed += (_, e) => { if (e.FromRemote) Console.WriteLine($"HR{e.Address} written"); };
await slave.StartAsync();

// Swap in the Rust engine — same interface
IModbusClient fast = NativeModbusClient.Create(o => o.UseTcp("192.168.1.10", 502));
```

## Simulator

`ModbusSimulator.CreateVirtualPlc(store)` map: IR0 temperature×10, IR1 humidity×10, IR2 pressure, IR3 rpm,
IR4-5 power kW (float32), IR6-7 energy kWh (float32), HR0 setpoint×10 (rw), HR1 production counter, HR2 alarm word,
HR10-21 device name, coil 0 motor (rw), coil 1 cooling pump, DI0 door, DI1 e-stop, DI2 high temperature.
Add your own: `simulator.AddSignal(ModbusTable.HoldingRegisters, 50, t => Math.Sin(t) * 100)`. Drive it
deterministically with `Tick(seconds)` in tests.

```bash
iotcom modbus serve --port 1502 --simulate
```

## Testing and interoperability

The shared vectors in `/conformance/modbus.json` (spec examples from the Modbus Application Protocol and Serial Line
specifications) run in both the C# and Rust suites, and a cross-language test checks that the Rust engine and the
managed framing produce identical bytes. Integration tests cover TCP sockets, pipelining, reconnect after a server
restart, RTU unit filtering and exception responses.

## Security and safety

Modbus has no authentication or encryption. Keep it on isolated networks, never expose port 502 to the internet, and
put a gateway in front for remote access. Writes move real equipment: use `AsReadOnly()` while commissioning; the CLI
requires `--allow-write` plus a confirmation.

## Limitations

- RTU timing relies on frame-length rules rather than the 3.5-character silence, which is not observable reliably on
  desktop OSes. Unknown function codes on RTU are resynchronised byte by byte.
- RS-485 direction control is expected from the adapter (auto-direction).
- Diagnostics (0x08) and file records (0x14/0x15) are reachable through `SendAsync` but have no typed helpers yet.

## Learn more

Notebook `notebooks/industrial/01-modbus.en.ipynb` · Gallery demo *Smart factory PLC* · `iotcom modbus --help`
