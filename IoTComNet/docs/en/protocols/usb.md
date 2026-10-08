---
title: USB and HID
translation-status: synced
---

# USB and HID

**Summary.** Many lab and field devices have no driver of their own: vendor-class gadgets, debug probes, CAN adapters,
relay boards and HID instruments. `IoTCom.Net.Transport.Usb` reaches them through `iotcom_usb`, a Rust library built on
nusb (raw transfers: WinUSB on Windows, usbfs on Linux, IOKit on macOS) and hidapi (HID reports), with the same C ABI
contract as the other native libraries. It provides:

- `UsbDevice` — an IoTCom endpoint: control IN/OUT on endpoint 0 (`UsbSetup` builds the SETUP packet), bulk and
  interrupt reads and writes, `StreamAsync` for an IN endpoint, traffic tap, and a read-only switch that refuses
  writes and control OUT requests;
- `UsbBulkTransport` / `UseUsbBulk(...)` — a bulk IN/OUT pair as a byte-stream transport, so any IoTCom protocol can run
  over a USB bridge;
- `HidDevice` — input, output and feature reports (read-only switch for output and feature writes) and
  `HidRelayBoard` for the common "USBRelayN" relay modules (16c0:05df);
- `UsbDevice.List()` / `HidDevice.List()` with `UsbIds` names for classes and well-known vid/pid pairs;
- `VirtualUsbBus` with a loopback device and a simulated relay board, for tests, notebooks, the Gallery and `--sim`.

## When to use it

- Talking to a vendor-class device or a firmware you wrote (TinyUSB, Zephyr, STM32 USB).
- Switching USB HID relay boards from a gateway or a test rig.
- Building on top of USB for DFU, debug probes or CAN adapters (gs_usb arrives in the next release).

## Installation

```bash
dotnet add package IoTCom.Net.Transport.Usb --prerelease      # also part of the IoTCom.Net meta-package
```

**Drivers and permissions.** HID devices need nothing extra. Raw transfers need access to the interface:

- **Windows:** the interface must use the WinUSB driver (many vendor devices ship with it; otherwise install it with
  Zadig). Interfaces owned by another driver cannot be claimed.
- **Linux:** add a udev rule such as `SUBSYSTEM=="usb", ATTRS{idVendor}=="1209", MODE="0660", GROUP="plugdev"`;
  `DetachKernelDriver = true` releases an interface claimed by a kernel driver.
- **macOS:** works for vendor-class interfaces; interfaces claimed by a system driver cannot be opened.

## Quickstart

```csharp
using IoTCom.Net.Transport.Usb;

foreach (var d in UsbDevice.List()) Console.WriteLine($"{d.Id} {d.Product} {d.Kind}");

await using var dev = UsbDevice.Create(o => o.UseDevice(0x1209, 0x0001));   // claims interface 0
await dev.ConnectAsync();
var version = await dev.ControlInAsync(UsbSetup.Vendor(0x01), 16);
await dev.WriteAsync(0x01, "ping"u8.ToArray());
var echo = await dev.ReadAsync(0x81, timeout: TimeSpan.FromSeconds(1));   // null on timeout

await using var hid = HidDevice.Create(o => o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId));
await hid.ConnectAsync();
await new HidRelayBoard(hid).SetAsync(1, on: true);
```

## Safety

`ReadOnly = true` on `UsbDevice` refuses bulk/interrupt writes and control OUT requests; on `HidDevice` it refuses
output and feature reports. `iotcom usb write` and `iotcom usb relay <n> on|off` require `--allow-write`, and the relay
command asks for confirmation against real hardware.

## Tools

```bash
iotcom usb list                                    # devices with vendor/product names and interface classes
iotcom usb hid                                     # HID collections with usage names
iotcom usb control --sim 1209:0001 0x01            # control IN (read-only)
iotcom usb write --sim 1209:0001 68656c6c6f --allow-write
iotcom usb relay                                   # states of a USB HID relay board
iotcom usb relay 2 on --allow-write
```

Gallery: *USB bench*. It lists this computer's devices without opening them.

## Testing

Tests run on the virtual bus: enumeration and class names, control IN (vendor request, device descriptor), stalls,
bulk echo, control OUT changing device behaviour, timeouts, wrong endpoint directions, unplugging, the bulk byte-stream
transport, the relay-board protocol and read-only modes. The native JSON is parsed in tests, and the native library
is smoke-tested where it is built. On the development machine it enumerated the real USB and HID devices. Rust unit
tests cover id parsing and `bmRequestType` decoding.

## Limitations

No isochronous transfers, hotplug events or USB device (gadget) role. Raw transfers depend on the platform driver
rules above. A native build that fails for a RID leaves the virtual bus working.

## Learn more

Notebook `notebooks/devices/15-usb.en.ipynb` · sample `samples/console/UsbRelay` · [native ABI](../native/rust-ffi.md)
