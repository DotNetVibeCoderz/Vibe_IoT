---
title: USB dan HID
translation-status: synced
---

# USB dan HID

**Ringkasan.** Banyak perangkat lab dan lapangan tidak punya driver sendiri: gadget kelas vendor, debug probe, adaptor
CAN, papan relay, dan instrumen HID. `IoTCom.Net.Transport.Usb` menjangkaunya lewat `iotcom_usb`, pustaka Rust di atas
nusb (transfer mentah: WinUSB di Windows, usbfs di Linux, IOKit di macOS) dan hidapi (report HID), dengan kontrak C ABI
yang sama seperti pustaka native lainnya. Paket ini menyediakan:

- `UsbDevice` — endpoint IoTCom: control IN/OUT di endpoint 0 (`UsbSetup` membangun paket SETUP), baca dan tulis bulk
  serta interrupt, `StreamAsync` untuk endpoint IN, traffic tap, dan saklar read-only yang menolak penulisan dan
  request control OUT;
- `UsbBulkTransport` / `UseUsbBulk(...)` — pasangan bulk IN/OUT sebagai transport aliran byte, sehingga protokol IoTCom
  apa pun bisa berjalan lewat jembatan USB;
- `HidDevice` — report input, output, dan feature (saklar read-only untuk penulisan output dan feature) serta
  `HidRelayBoard` untuk modul relay "USBRelayN" yang umum (16c0:05df);
- `UsbDevice.List()` / `HidDevice.List()` dengan nama `UsbIds` untuk kelas dan pasangan vid/pid yang dikenal;
- `VirtualUsbBus` dengan perangkat loopback dan papan relay simulasi, untuk pengujian, notebook, Gallery, dan `--sim`.

## Kapan dipakai

- Berbicara dengan perangkat kelas vendor atau firmware buatan Anda (TinyUSB, Zephyr, STM32 USB).
- Menyalakan papan relay USB HID dari gateway atau rig pengujian.
- Membangun di atas USB untuk DFU, debug probe, atau adaptor CAN (gs_usb hadir di rilis berikutnya).

## Instalasi

```bash
dotnet add package IoTCom.Net.Transport.Usb --prerelease      # juga bagian dari meta-package IoTCom.Net
```

**Driver dan izin.** Perangkat HID tidak memerlukan apa pun. Transfer mentah memerlukan akses ke interface:

- **Windows:** interface harus memakai driver WinUSB (banyak perangkat vendor sudah membawanya; jika tidak, pasang
  dengan Zadig). Interface yang dimiliki driver lain tidak bisa diklaim.
- **Linux:** tambahkan aturan udev seperti `SUBSYSTEM=="usb", ATTRS{idVendor}=="1209", MODE="0660", GROUP="plugdev"`;
  `DetachKernelDriver = true` melepas interface yang diklaim driver kernel.
- **macOS:** berfungsi untuk interface kelas vendor; interface yang diklaim driver sistem tidak bisa dibuka.

## Mulai cepat

```csharp
using IoTCom.Net.Transport.Usb;

foreach (var d in UsbDevice.List()) Console.WriteLine($"{d.Id} {d.Product} {d.Kind}");

await using var dev = UsbDevice.Create(o => o.UseDevice(0x1209, 0x0001));   // mengklaim interface 0
await dev.ConnectAsync();
var version = await dev.ControlInAsync(UsbSetup.Vendor(0x01), 16);
await dev.WriteAsync(0x01, "ping"u8.ToArray());
var echo = await dev.ReadAsync(0x81, timeout: TimeSpan.FromSeconds(1));   // null bila timeout

await using var hid = HidDevice.Create(o => o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId));
await hid.ConnectAsync();
await new HidRelayBoard(hid).SetAsync(1, on: true);
```

## Keamanan

`ReadOnly = true` pada `UsbDevice` menolak penulisan bulk/interrupt dan request control OUT; pada `HidDevice` menolak
report output dan feature. `iotcom usb write` dan `iotcom usb relay <n> on|off` mewajibkan `--allow-write`, dan perintah
relay meminta konfirmasi terhadap perangkat keras sungguhan.

## Alat

```bash
iotcom usb list                                    # perangkat dengan nama vendor/produk dan kelas interface
iotcom usb hid                                     # koleksi HID dengan nama usage
iotcom usb control --sim 1209:0001 0x01            # control IN (hanya-baca)
iotcom usb write --sim 1209:0001 68656c6c6f --allow-write
iotcom usb relay                                   # status papan relay USB HID
iotcom usb relay 2 on --allow-write
```

Gallery: *Meja kerja USB*. Demo ini mendaftar perangkat komputer ini tanpa membukanya.

## Pengujian

Pengujian berjalan di bus virtual: enumerasi dan nama kelas, control IN (vendor request, device descriptor), stall,
echo bulk, control OUT yang mengubah perilaku perangkat, timeout, arah endpoint yang salah, pencabutan perangkat,
transport aliran byte bulk, protokol papan relay, dan mode read-only. JSON native diurai dalam pengujian, dan pustaka
native diuji singkat di mesin tempat ia dibangun. Di mesin pengembangan ia mengenumerasi perangkat USB dan HID
sungguhan. Uji unit Rust mencakup penguraian id dan dekode `bmRequestType`.

## Keterbatasan

Belum ada transfer isochronous, event hotplug, atau peran perangkat USB (gadget). Transfer mentah bergantung pada
aturan driver platform di atas. Build native yang gagal untuk suatu RID membuat bus virtual tetap berfungsi.

## Pelajari lebih lanjut

Notebook `notebooks/devices/15-usb.id.ipynb` · sampel `samples/console/UsbRelay` · [ABI native](../native/rust-ffi.md)
