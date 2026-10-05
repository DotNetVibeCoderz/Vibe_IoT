---
title: CAN and CAN FD
translation-status: synced
---

# CAN and CAN FD

**Summary.** CAN is the field bus of vehicles, agricultural and construction machines, e-bikes, medical devices and
industrial drives. Frames carry an 11-bit or 29-bit identifier and 0–8 data bytes; CAN FD extends the data field to
64 bytes with a faster data phase. `IoTCom.Net.Transport.Can` gives every backend one `ICanBus` interface with
filtered readers, the traffic tap and metrics. The backends are Linux SocketCAN, slcan USB adapters and an
in-process virtual bus.

## When to use it

- Logging or decoding a vehicle or machine bus (`candump`-style).
- Sending commands to CAN devices (motor controllers, BMS, sensors).
- Carrying higher protocols: [ISO-TP, UDS and OBD-II](uds.md), and in the future CANopen and J1939.
- Testing CAN software without hardware on a virtual bus.

## Backends

| URI | Backend | Platforms | Notes |
|---|---|---|---|
| `socketcan:can0` | `SocketCanBus` | Linux | any SocketCAN interface (`can0`, `vcan0`, `slcan0`); CAN FD supported |
| `slcan:COM5`, `slcan:/dev/ttyACM0` | `SlcanBus` | all | Lawicel/slcan adapters: CANable, CANtact, USBtin; CAN FD with CANable 2 firmware |
| `slcan-tcp:host:port` | `SlcanBus` | all | slcan over TCP (serial servers, `iotcom can simulate`) |
| `virtual:name` | `VirtualCanBus` | all | in-process; every node on the same name sees the others' frames |

PCAN, Kvaser and gs_usb backends are on the [roadmap](../../../PLAN.md).

## Installation

```bash
dotnet add package IoTCom.Net.Transport.Can --prerelease    # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Transport.Can;

await using var bus = await CanBus.OpenAsync("socketcan:can0");
using var reader = bus.OpenReader(new CanFilter(0x7E8, 0x7F8));     // 0x7E8–0x7EF
await bus.SendAsync(CanFrame.Parse("7DF#02010C"));                  // OBD-II: engine rpm
CanFrame answer = await reader.ReadAsync();
Console.WriteLine(answer);                                           // 7E8#04410C1AF8
```

## Frames

`CanFrame` is an immutable struct: `Id`, `Data`, `Flags` (`Extended`, `Remote`, `Fd`, `BitRateSwitch`,
`ErrorStateIndicator`, `Error`), `Dlc` and `Timestamp`. The constructor validates identifier width and length.
Text uses the can-utils notation:

| Text | Meaning |
|---|---|
| `123#DEADBEEF` | 11-bit id 0x123, 4 bytes |
| `18DAF110#0322F190` | 29-bit id |
| `123#R` / `123#R4` | remote frame (length 4) |
| `123##1001122…` | CAN FD; the digit after `##` is the flags (1 = BRS, 2 = ESI) |

`CanDlc.Pad(data)` pads data to the next valid CAN FD length (12, 16, 20, 24, 32, 48, 64).

## Receiving

- `OpenReader(filter)` returns a buffered `CanReader`: `ReadAsync`, `ReadAsync(timeout)`, `TryRead`,
  `ReadAllAsync`. Each reader has its own queue. When a queue is full, its oldest frame is dropped and counted in
  `Dropped`.
- `FrameReceived` is raised on the receive thread for every frame. Keep handlers short.
- `CanFilter.Exact(id)` matches one identifier of the inferred width. `new CanFilter(id, mask)` matches a range.

## Configuration (`CanBusOptions`)

| Option | Default | Description |
|---|---|---|
| `Bitrate` | 500 000 | nominal bit rate (slcan; for SocketCAN set it with `ip link`) |
| `DataBitrate` | 2 000 000 | CAN FD data phase (slcan `Y` command) |
| `Fd` | false | enable CAN FD frames |
| `ListenOnly` | false | open silently; `SendAsync` throws |
| `ReceiveOwnMessages` | false | deliver our own frames to our readers too |
| `ReaderCapacity` | 4096 | frames buffered per reader |

## Setting up hardware

```bash
# Linux, native controller or USB adapter with a SocketCAN driver (gs_usb, PCAN, Kvaser):
sudo ip link set can0 up type can bitrate 500000
# virtual interface for tests:
sudo modprobe vcan && sudo ip link add vcan0 type vcan && sudo ip link set vcan0 up
```

slcan adapters need no driver on any OS. Pass the serial port and the bit rate, and `SlcanBus` sends `S6`/`O` itself.

## Simulator and tools

```bash
iotcom can list                                   # SocketCAN interfaces and serial ports
iotcom can dump --can socketcan:can0 --filter 7E8:7F8
iotcom can send --can slcan:COM5 7DF#02010C
iotcom can simulate --port 20100                  # ECU simulator behind an emulated slcan adapter on TCP
iotcom can dump --can slcan-tcp:127.0.0.1:20100
```

`SlcanAdapterSimulator` emulates an slcan adapter on any byte stream, and `VirtualCanNetwork` connects nodes in
memory. Together they let you test the slcan path end to end in unit tests.

## Testing and interoperability

Tests cover the can-utils notation, DLC tables, filters, fan-out on the virtual bus, the slcan codec (including
CAN FD and timestamp suffixes), a full slcan session through the emulated adapter, and the SocketCAN
`can_frame`/`canfd_frame` layouts. CI also runs a live `vcan0` round trip on Linux when the kernel module is
available.

## Security

CAN has no authentication: any node can send any identifier. Sending frames to a running vehicle or machine can
cause dangerous behaviour. Use `ListenOnly` for monitoring, and send only on benches or with the equipment in a
safe state.

## Limitations

There are no PCAN, Kvaser or Vector backends yet; use SocketCAN drivers on Linux or an slcan adapter. Error frames
are surfaced as flagged frames, but bus-off recovery is left to the driver. CAN XL is not supported.

## Learn more

[ISO-TP, UDS and OBD-II](uds.md) · Gallery demo *Vehicle diagnostics* · notebook
`notebooks/automotive/06-can-uds.en.ipynb` · `iotcom can --help`
