---
title: SWD/JTAG, RTT and DFU
translation-status: synced
---

# Probe and flash microcontrollers (SWD/JTAG, RTT, DFU)

**Summary.** `IoTCom.Net.Debug` reaches a microcontroller two ways: through a **debug probe** on its SWD or JTAG port, or
through its **USB DFU bootloader**. It provides:

- `DebugProbe`: attach to a chip by its probe-rs target name (`STM32F411CEUx`, `nRF52840_xxAA`, `RP2040`, `esp32c3`…),
  read memory while the core runs, read the core state and PC, and — after `AllowWrites()` — flash (erase, program,
  verify, reset), erase all, write memory, halt, run and reset. The native backend wraps **probe-rs**, so CMSIS-DAP v1/v2,
  ST-Link v2/v3, SEGGER J-Link, ESP32 USB-JTAG, FTDI and WCH-Link probes and the flash algorithms of hundreds of chips
  work out of the box.
- `RttClient`: SEGGER RTT over any `ITargetMemory` — finds the control block in RAM (or at the `_SEGGER_RTT` address),
  lists channels, reads up channels (moving only the read offset) and writes down channels.
- `DfuClient`: USB DFU 1.1 with ST's DfuSe extensions — status, upload, download with page erase and set-address,
  leave — over `IoTCom.Net.Transport.Usb`. `DfuSeLayout` parses the memory layout a DfuSe device publishes.
- Firmware images: `FirmwareImage` with Intel HEX (`IntelHex`), DFU files with the suffix and DfuSe targets (`DfuFile`),
  ELF32 PT_LOAD segments (`Elf`) and raw binaries; `Crc32`.
- Simulators: `VirtualMcu` (an STM32F411-like chip running a demo firmware that logs over RTT and takes commands),
  `VirtualProbeBackend` (a CMSIS-DAP probe wired to it) and `VirtualDfuDevice` (its DfuSe system bootloader).

Following the design (curation tier 2), probe access wraps probe-rs in the native library `iotcom_probe`; RTT, DFU and
the firmware formats are managed C# (they run over memory access and USB control transfers), and the Rust crate
`iotcom-firmware` is their fuzzed twin, sharing 27 vectors in `/conformance/firmware.json`.

## When to use it

- Production programming and end-of-line test stations written in .NET: flash, verify, read a serial number, check RTT.
- Logging from firmware without a UART, from a .NET dashboard or test harness.
- Field updates through a board's DFU bootloader without a probe.
- Inspecting firmware files: segments, CRCs, DFU suffixes, conversions to Intel HEX.

## Installation

```bash
dotnet add package IoTCom.Net.Debug --prerelease    # also part of the IoTCom.Net meta-package
```

Probes on Windows need a WinUSB-compatible driver (ST-Link and J-Link vendor drivers work; CMSIS-DAP v2 is driverless;
others via Zadig). On Linux install the probe-rs udev rules. DFU devices use the same USB stack as
`IoTCom.Net.Transport.Usb` (WinUSB on Windows).

## Quickstart

Flash and read the RTT log:

```csharp
using IoTCom.Net.Debug;

await using var probe = DebugProbe.Create(o => o.UseTarget("STM32F411CEUx").AllowWrites());
await probe.FlashAsync(FirmwareImage.Load("firmware.elf"), new Progress<FlashProgress>(p => Console.WriteLine($"{p.Phase} {p.Fraction:P0}")));
var rtt = await probe.AttachRttAsync(0x20000000, 128 * 1024);
while (true) Console.Write(await rtt.ReadTextAsync());
```

Read-only inspection (the default):

```csharp
await using var probe = DebugProbe.Create(o => o.UseTarget("nRF52840_xxAA"));
var (state, pc) = await probe.CoreAsync();
var deviceId = await probe.ReadMemoryAsync(0x10000060, 8);   // FICR DEVICEID
```

DfuSe update through the STM32 system bootloader:

```csharp
await using var dfu = DfuClient.Create(o => o.UseDevice(0x0483, 0xDF11).AllowWrites());
await dfu.ConnectAsync();
Console.WriteLine(dfu.Layout!.Describe());                  // Internal Flash: 08000000+4x16384rew,...
await dfu.DownloadAsync(DfuFile.Parse(File.ReadAllBytes("firmware.dfu")).Image);
await dfu.LeaveAsync();
```

## Safety

Flashing and erasing replace firmware; halting a core stops a running machine; writing memory can change peripherals.
`DebugProbe` and `DfuClient` are **read-only by default**: memory reads, core status, RTT reads, DFU status and uploads
only. RTT reads still update the read offset in target RAM — that is how the protocol works. The CLI needs
`--allow-write` and asks before touching a real board; the Gallery has a write lock. Erasing all flash of a chip with
read-out protection or option bytes can brick or mass-erase it; check the chip's reference manual.

## Tools

```bash
iotcom probe list                                            # probes (probe-rs)
iotcom probe info --target STM32F411CEUx                    # or --sim
iotcom probe read 0x08000000 64 --sim
iotcom probe flash firmware.elf --target STM32F411CEUx --allow-write
iotcom probe flash --sim --version 1.1.0 --allow-write
iotcom probe rtt --sim [--send "led on" --allow-write]
iotcom probe control reset|halt|run|reset-halt|erase-all --sim --allow-write
iotcom probe image firmware.dfu [--to-hex out.hex]
iotcom dfu list | info --sim | upload backup.bin --sim | download firmware.dfu --leave --allow-write
```

The VS Code extension decodes `ihex`, `dfu-suffix` and `rtt` (a control-block dump). Gallery: *Probe and flash firmware*
(design use case 12).

## Testing

Firmware tests cover the CRC check value, Intel HEX across 64 KiB with entry points and malformed records, DFU suffix
CRCs, DfuSe round trips, ELF load addresses and DfuSe layouts; the 27 shared vectors add segment addresses, two-target
DfuSe files, malformed suffixes and elements, layouts with several areas, and RTT control blocks with wrapped and
invalid buffers; the Rust twin is fuzzed (`firmware`, 3 M local runs; it found a UTF-8 slicing panic in layout parsing,
fixed). Probe tests run against the simulated MCU: memory reads, RTT logs and offsets, read-only refusals, flashing a
new version and seeing it boot, RTT commands, halt and run, mass erase and lock-up, out-of-range images and a wrong
target name. DFU tests download a DfuSe image, upload it back, leave into the new firmware, and check read-only and
range refusals. The native library has its own tests (listing without probes, argument checks) and binding drift tests.

## Limitations

No GDB server, breakpoints, stepping or stack unwinding (use probe-rs or your IDE for interactive debugging); RTT on
64-bit cores and defmt decoding are not implemented; SWO trace is not exposed. DFU: no DfuSe read-unprotect, option
bytes or plain-DFU manifestation-tolerant devices beyond the basic flow. The native library has not been run against a
physical probe yet in CI (none is attached to the runners).

## Learn more

Notebook `notebooks/devices/30-probe-flash.en.ipynb` · sample `samples/console/ProbeFlash` · [USB](usb.md) ·
[framing and CRC](framing.md)
