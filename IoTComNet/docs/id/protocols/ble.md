---
title: Bluetooth LE
translation-status: synced
---

# Bluetooth Low Energy (central)

**Ringkasan.** Perangkat wearable, beacon, sensor, smart plug, dan banyak antarmuka commissioning berbicara Bluetooth Low
Energy. `IoTCom.Net.Transport.Ble` adalah **central** BLE: memindai advertisement, tersambung ke periferal, menemukan
layanan GATT-nya, lalu membaca, menulis, dan subscribe ke characteristic. Sesuai desain (Rust untuk akses perangkat
keras), radio dijangkau lewat `iotcom_ble`, pustaka Rust di atas btleplug — WinRT di Windows, BlueZ (D-Bus) di Linux,
CoreBluetooth di macOS — dengan kontrak C ABI yang sama seperti pustaka native lainnya. Paket ini menambahkan:

- `BleCentral` — endpoint IoTCom: `ScanAsync`, `WatchAsync` (`IAsyncEnumerable` berisi advertisement), `OpenAsync` →
  `BlePeripheral` dengan `ReadAsync`, `WriteAsync` (diblokir dalam mode read-only), `SubscribeAsync` (`IAsyncEnumerable`
  berisi nilai); lalu lintas GATT di traffic tap;
- codec: `BleUuid` (UUID SIG 16/32-bit dan namanya), `AdvertisingData` (struktur AD, frame lane), `IBeacon`,
  `EddystoneFrame` (UID, URL, TLM), `GattValue` (detak jantung, baterai, environmental sensing, string);
- `VirtualBleNetwork` — radio simulasi dengan tali detak jantung, sensor rumah kaca, iBeacon, dan smart plug, digerakkan
  oleh `VirtualBleSimulator`, untuk pengujian, notebook, dan Gallery.

## Kapan dipakai

- Membaca wearable dan sensor BLE dari gateway atau alat desktop.
- Commissioning perangkat yang membuka layanan GATT vendor (layanan bergaya Nordic UART, characteristic konfigurasi).
- Deteksi kehadiran di dalam ruangan dengan iBeacon / Eddystone.

## Instalasi

```bash
dotnet add package IoTCom.Net.Transport.Ble --prerelease      # juga bagian dari meta-package IoTCom.Net
```

Di Linux, BlueZ harus berjalan dan pengguna perlu akses ke D-Bus sistem (biasanya grup `bluetooth`). Di macOS, pemindaian
pertama memunculkan permintaan izin Bluetooth untuk aplikasi host.

## Mulai cepat

```csharp
using IoTCom.Net.Transport.Ble;

await using var ble = BleCentral.Create(o => { o.UseNative(); o.ReadOnly = true; });
await ble.ConnectAsync();                                              // membuka radio

var straps = await ble.ScanAsync(TimeSpan.FromSeconds(10), [BleUuid.FromShort(0x180D)]);
await using var strap = await ble.OpenAsync(straps[0].Id);
Console.WriteLine(GattValue.Describe(BleUuid.Parse("2a19"), await strap.ReadAsync("2a19")));   // "87 %"

await foreach (var v in strap.SubscribeAsync(BleUuid.FromShort(0x2A37)))
    Console.WriteLine(GattValue.ParseHeartRate(v).BeatsPerMinute);
```

Periferal harus sudah terlihat dalam pemindaian sebelum `OpenAsync`. Id berupa alamat Bluetooth di Windows dan Linux,
dan UUID per-host di macOS.

## Keamanan

Dengan `ReadOnly = true`, `WriteAsync` melempar `ReadOnlyModeException` sebelum apa pun dikirim. `iotcom ble write`
mewajibkan `--allow-write` dan konfirmasi terhadap perangkat sungguhan.

## Alat

```bash
iotcom ble scan                                     # radio sungguhan (5 dtk); --service 180d menyaring
iotcom ble scan --sim                               # ruangan virtual
iotcom ble services --sim E8:4F:25:10:7A:33         # pohon GATT dengan nilai terurai
iotcom ble watch --sim C4:7C:8D:6A:21:0F 2a37       # notifikasi detak jantung
iotcom ble write --sim D0:8E:3A:55:10:C2 6e400002-b5a3-f393-e0a9-e50e24dcca9e 01 --allow-write
```

Ekstensi VS Code mengurai advertising data mentah (`ble-adv`). Gallery: *Perangkat Bluetooth di sekitar*.

## Pengujian

Uji codec memakai tata letak yang dipublikasikan: flag detak jantung dengan nilai 8/16-bit dan interval RR, satuan
environmental sensing, round trip advertisement tali detak jantung, frame iBeacon, Eddystone URL/UID/TLM, dan event JSON
dari pustaka native. Uji central berjalan di radio virtual: pemindaian dengan dan tanpa filter layanan, pembacaan,
notifikasi, periferal yang keluar jangkauan, beacon yang tidak bisa disambung, dan mode read-only. Pustaka native diuji
singkat di mesin tempat ia dibangun: membuka radio atau melaporkan bahwa tidak ada; di mesin pengembangan ia menerima
advertisement sungguhan lewat WinRT. Uji unit Rust mencakup ekspansi UUID dan helper JSON.

## Keterbatasan

Hanya peran central (tanpa peran periferal/advertiser), tanpa API pairing atau bonding, tanpa kanal L2CAP, dan tanpa
kendali MTU atau PHY. Build Linux menyertakan libdbus; jika build native tidak tersedia untuk suatu RID, radio virtual
tetap berfungsi.

## Pelajari lebih lanjut

Notebook `notebooks/devices/14-ble.id.ipynb` · sampel `samples/console/BleHeartRate` · [ABI native](../native/rust-ffi.md)
