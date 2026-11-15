---
title: SWD/JTAG, RTT, dan DFU
translation-status: synced
---

# Probe dan flash mikrokontroler (SWD/JTAG, RTT, DFU)

**Ringkasan.** `IoTCom.Net.Debug` menjangkau mikrokontroler dengan dua cara: lewat **probe debug** di port SWD atau
JTAG-nya, atau lewat **bootloader USB DFU**-nya. Paket ini menyediakan:

- `DebugProbe`: terhubung ke chip berdasarkan nama target probe-rs (`STM32F411CEUx`, `nRF52840_xxAA`, `RP2040`,
  `esp32c3`…), membaca memori saat core berjalan, membaca state core dan PC, serta — setelah `AllowWrites()` — flash
  (hapus, program, verifikasi, reset), hapus semua, tulis memori, halt, run, dan reset. Backend native membungkus
  **probe-rs**, sehingga probe CMSIS-DAP v1/v2, ST-Link v2/v3, SEGGER J-Link, ESP32 USB-JTAG, FTDI, dan WCH-Link serta
  algoritma flash ratusan chip langsung bisa dipakai.
- `RttClient`: SEGGER RTT di atas `ITargetMemory` apa pun — menemukan control block di RAM (atau di alamat
  `_SEGGER_RTT`), mendaftar kanal, membaca kanal up (hanya memajukan offset baca), dan menulis kanal down.
- `DfuClient`: USB DFU 1.1 dengan ekstensi DfuSe dari ST — status, upload, download dengan hapus halaman dan set
  alamat, leave — di atas `IoTCom.Net.Transport.Usb`. `DfuSeLayout` mengurai tata letak memori yang diterbitkan perangkat
  DfuSe.
- Image firmware: `FirmwareImage` dengan Intel HEX (`IntelHex`), file DFU dengan suffix dan target DfuSe (`DfuFile`),
  segmen PT_LOAD ELF32 (`Elf`), dan biner mentah; `Crc32`.
- Simulator: `VirtualMcu` (chip mirip STM32F411 yang menjalankan firmware demo yang mencatat log lewat RTT dan menerima
  perintah), `VirtualProbeBackend` (probe CMSIS-DAP yang tersambung ke chip itu), dan `VirtualDfuDevice` (bootloader
  sistem DfuSe-nya).

Sesuai desain (tingkat kurasi 2), akses probe membungkus probe-rs di pustaka native `iotcom_probe`; RTT, DFU, dan format
firmware ditulis dalam C# terkelola (berjalan di atas akses memori dan transfer kontrol USB), dan crate Rust
`iotcom-firmware` adalah kembaran yang di-fuzz, berbagi 27 vektor di `/conformance/firmware.json`.

## Kapan dipakai

- Stasiun pemrograman produksi dan tes akhir lini yang ditulis dalam .NET: flash, verifikasi, baca nomor seri, cek RTT.
- Mencatat log dari firmware tanpa UART, dari dashboard .NET atau test harness.
- Pembaruan di lapangan lewat bootloader DFU board tanpa probe.
- Memeriksa file firmware: segmen, CRC, suffix DFU, konversi ke Intel HEX.

## Instalasi

```bash
dotnet add package IoTCom.Net.Debug --prerelease    # juga bagian dari meta-package IoTCom.Net
```

Probe di Windows membutuhkan driver yang kompatibel dengan WinUSB (driver vendor ST-Link dan J-Link berfungsi; CMSIS-DAP
v2 tanpa driver; lainnya lewat Zadig). Di Linux pasang aturan udev probe-rs. Perangkat DFU memakai stack USB yang sama
dengan `IoTCom.Net.Transport.Usb` (WinUSB di Windows).

## Mulai cepat

Flash lalu baca log RTT:

```csharp
using IoTCom.Net.Debug;

await using var probe = DebugProbe.Create(o => o.UseTarget("STM32F411CEUx").AllowWrites());
await probe.FlashAsync(FirmwareImage.Load("firmware.elf"), new Progress<FlashProgress>(p => Console.WriteLine($"{p.Phase} {p.Fraction:P0}")));
var rtt = await probe.AttachRttAsync(0x20000000, 128 * 1024);
while (true) Console.Write(await rtt.ReadTextAsync());
```

Pemeriksaan hanya-baca (bawaan):

```csharp
await using var probe = DebugProbe.Create(o => o.UseTarget("nRF52840_xxAA"));
var (state, pc) = await probe.CoreAsync();
var deviceId = await probe.ReadMemoryAsync(0x10000060, 8);   // FICR DEVICEID
```

Pembaruan DfuSe lewat bootloader sistem STM32:

```csharp
await using var dfu = DfuClient.Create(o => o.UseDevice(0x0483, 0xDF11).AllowWrites());
await dfu.ConnectAsync();
Console.WriteLine(dfu.Layout!.Describe());                  // Internal Flash: 08000000+4x16384rew,...
await dfu.DownloadAsync(DfuFile.Parse(File.ReadAllBytes("firmware.dfu")).Image);
await dfu.LeaveAsync();
```

## Keselamatan

Flash dan hapus mengganti firmware; menghentikan core menghentikan mesin yang sedang berjalan; menulis memori bisa
mengubah periferal. `DebugProbe` dan `DfuClient` **hanya-baca secara bawaan**: hanya baca memori, status core, baca RTT,
status DFU, dan upload. Pembacaan RTT tetap memperbarui offset baca di RAM target — begitulah protokolnya bekerja. CLI
membutuhkan `--allow-write` dan meminta konfirmasi sebelum menyentuh board sungguhan; Gallery punya kunci tulis.
Menghapus seluruh flash chip yang memiliki read-out protection atau option byte bisa membuatnya brick atau terhapus
massal; periksa reference manual chip.

## Alat

```bash
iotcom probe list                                            # probe (probe-rs)
iotcom probe info --target STM32F411CEUx                    # atau --sim
iotcom probe read 0x08000000 64 --sim
iotcom probe flash firmware.elf --target STM32F411CEUx --allow-write
iotcom probe flash --sim --version 1.1.0 --allow-write
iotcom probe rtt --sim [--send "led on" --allow-write]
iotcom probe control reset|halt|run|reset-halt|erase-all --sim --allow-write
iotcom probe image firmware.dfu [--to-hex out.hex]
iotcom dfu list | info --sim | upload backup.bin --sim | download firmware.dfu --leave --allow-write
```

Ekstensi VS Code mengurai `ihex`, `dfu-suffix`, dan `rtt` (dump control block). Gallery: *Probe dan flash firmware*
(use case desain 12).

## Pengujian

Tes firmware mencakup nilai cek CRC, Intel HEX melintasi 64 KiB dengan entry point dan record rusak, CRC suffix DFU,
round trip DfuSe, alamat muat ELF, dan tata letak DfuSe; 27 vektor bersama menambahkan alamat segmen, file DfuSe dengan
dua target, suffix dan elemen rusak, tata letak dengan beberapa area, serta control block RTT dengan buffer yang wrap dan
tidak valid; kembaran Rust di-fuzz (`firmware`, 3 juta run lokal; fuzzing menemukan panic pemotongan UTF-8 saat mengurai
tata letak, sudah diperbaiki). Tes probe berjalan terhadap MCU simulasi: baca memori, log dan offset RTT, penolakan
hanya-baca, flash versi baru lalu melihatnya booting, perintah RTT, halt dan run, hapus massal dan lock-up, image di luar
jangkauan, dan nama target yang salah. Tes DFU mengunduh image DfuSe, mengunggahnya kembali, keluar ke firmware baru,
serta memeriksa penolakan hanya-baca dan jangkauan. Pustaka native punya tes sendiri (daftar tanpa probe, pemeriksaan
argumen) dan tes binding drift.

## Batasan

Tidak ada server GDB, breakpoint, stepping, atau stack unwinding (pakai probe-rs atau IDE untuk debugging interaktif);
RTT pada core 64-bit dan decoding defmt belum diimplementasikan; trace SWO tidak diekspos. DFU: tidak ada read-unprotect
DfuSe, option byte, atau perangkat DFU biasa yang manifestation-tolerant di luar alur dasar. Pustaka native belum diuji
dengan probe fisik di CI (tidak ada yang terpasang di runner).

## Pelajari lebih lanjut

Notebook `notebooks/devices/30-probe-flash.id.ipynb` · sampel `samples/console/ProbeFlash` · [USB](usb.md) ·
[framing dan CRC](framing.md)
