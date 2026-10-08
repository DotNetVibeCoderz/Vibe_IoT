---
title: Bluetooth LE
translation-status: synced
---

# Bluetooth Low Energy (central)

**Summary.** Wearables, beacons, sensors, smart plugs and many commissioning interfaces speak Bluetooth Low Energy.
`IoTCom.Net.Transport.Ble` is a BLE **central**: it scans advertisements, connects to peripherals, discovers their
GATT services and reads, writes and subscribes to characteristics. Following the design (Rust for hardware access),
the radio is reached through `iotcom_ble`, a Rust library built on btleplug — WinRT on Windows, BlueZ (D-Bus) on Linux,
CoreBluetooth on macOS — with the same C ABI contract as the other native libraries. The package adds:

- `BleCentral` — the IoTCom endpoint: `ScanAsync`, `WatchAsync` (`IAsyncEnumerable` of advertisements), `OpenAsync` →
  `BlePeripheral` with `ReadAsync`, `WriteAsync` (blocked in read-only mode), `SubscribeAsync` (`IAsyncEnumerable` of
  values); GATT traffic on the traffic tap;
- codecs: `BleUuid` (16/32-bit SIG UUIDs and names), `AdvertisingData` (AD structures, frame lane), `IBeacon`,
  `EddystoneFrame` (UID, URL, TLM), `GattValue` (heart rate, battery, environmental sensing, strings);
- `VirtualBleNetwork` — a simulated radio with a heart-rate strap, a greenhouse sensor, an iBeacon and a smart plug,
  driven by `VirtualBleSimulator`, for tests, notebooks and the Gallery.

## When to use it

- Reading wearables and BLE sensors from a gateway or a desktop tool.
- Commissioning devices that expose a vendor GATT service (Nordic UART-style services, configuration characteristics).
- Indoor presence with iBeacon / Eddystone.

## Installation

```bash
dotnet add package IoTCom.Net.Transport.Ble --prerelease      # also part of the IoTCom.Net meta-package
```

On Linux, BlueZ must be running and the user needs access to the system D-Bus (usually the `bluetooth` group). On
macOS the first scan triggers the Bluetooth permission prompt for the host application.

## Quickstart

```csharp
using IoTCom.Net.Transport.Ble;

await using var ble = BleCentral.Create(o => { o.UseNative(); o.ReadOnly = true; });
await ble.ConnectAsync();                                              // opens the radio

var straps = await ble.ScanAsync(TimeSpan.FromSeconds(10), [BleUuid.FromShort(0x180D)]);
await using var strap = await ble.OpenAsync(straps[0].Id);
Console.WriteLine(GattValue.Describe(BleUuid.Parse("2a19"), await strap.ReadAsync("2a19")));   // "87 %"

await foreach (var v in strap.SubscribeAsync(BleUuid.FromShort(0x2A37)))
    Console.WriteLine(GattValue.ParseHeartRate(v).BeatsPerMinute);
```

Peripherals must have been seen in a scan before `OpenAsync`. Ids are Bluetooth addresses on Windows and Linux and
per-host UUIDs on macOS.

## Safety

With `ReadOnly = true`, `WriteAsync` throws `ReadOnlyModeException` before anything is sent. `iotcom ble write`
requires `--allow-write` and a confirmation against real devices.

## Tools

```bash
iotcom ble scan                                     # the real radio (5 s); --service 180d filters
iotcom ble scan --sim                               # the virtual room
iotcom ble services --sim E8:4F:25:10:7A:33         # GATT tree with decoded values
iotcom ble watch --sim C4:7C:8D:6A:21:0F 2a37       # heart-rate notifications
iotcom ble write --sim D0:8E:3A:55:10:C2 6e400002-b5a3-f393-e0a9-e50e24dcca9e 01 --allow-write
```

The VS Code extension decodes raw advertising data (`ble-adv`). Gallery: *Nearby Bluetooth devices*.

## Testing

Codec tests use published layouts: heart-rate flags with 8/16-bit values and RR intervals, environmental sensing
units, a typical strap advertisement round trip, an iBeacon frame, Eddystone URL/UID/TLM, and the JSON events of
the native library. Central tests run on the virtual radio: scanning with and without a service filter, reading,
notifications, a peripheral going out of range, non-connectable beacons, and read-only mode. The native library is
smoke-tested where it is built: it opens the radio or reports that none is available; on the development machine
it received real advertisements through WinRT. Rust unit tests cover UUID expansion and the JSON helpers.

## Limitations

Central role only (no peripheral/advertiser role), no pairing or bonding API, no L2CAP channels, and no MTU or PHY
control. Linux builds vendor libdbus; if a native build is missing for a RID, the virtual radio still works.

## Learn more

Notebook `notebooks/devices/14-ble.en.ipynb` · sample `samples/console/BleHeartRate` · [native ABI](../native/rust-ffi.md)
