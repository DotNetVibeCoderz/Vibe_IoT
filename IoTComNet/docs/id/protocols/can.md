---
title: CAN dan CAN FD
translation-status: synced
---

# CAN dan CAN FD

**Ringkasan.** CAN adalah bus lapangan untuk kendaraan, mesin pertanian dan konstruksi, sepeda listrik, perangkat
medis, dan penggerak industri. Setiap frame membawa identifier 11-bit atau 29-bit dan 0–8 byte data; CAN FD
memperluas data hingga 64 byte dengan fase data yang lebih cepat. `IoTCom.Net.Transport.Can` memberi semua backend
satu antarmuka `ICanBus` dengan reader terfilter, traffic tap, dan metrik. Backend yang tersedia adalah Linux
SocketCAN, adapter USB slcan, dan bus virtual di dalam proses.

## Kapan dipakai

- Merekam atau mengurai bus kendaraan atau mesin (gaya `candump`).
- Mengirim perintah ke perangkat CAN (pengendali motor, BMS, sensor).
- Membawa protokol di atasnya: [ISO-TP, UDS, dan OBD-II](uds.md), [CANopen](canopen.md), serta [SAE J1939](j1939.md).
- Menguji perangkat lunak CAN tanpa perangkat keras di bus virtual.

## Backend

| URI | Backend | Platform | Catatan |
|---|---|---|---|
| `socketcan:can0` | `SocketCanBus` | Linux | antarmuka SocketCAN apa pun (`can0`, `vcan0`, `slcan0`); mendukung CAN FD |
| `slcan:COM5`, `slcan:/dev/ttyACM0` | `SlcanBus` | semua | adapter Lawicel/slcan: CANable, CANtact, USBtin; CAN FD dengan firmware CANable 2 |
| `slcan-tcp:host:port` | `SlcanBus` | semua | slcan lewat TCP (serial server, `iotcom can simulate`) |
| `gsusb:`, `gsusb:1d50:606f:SERIAL#1` | `GsUsbCanBus` | semua | firmware candleLight / gs_usb (CANable 2, CANtact Pro, …) lewat USB mentah; bit timing dihitung dari clock adapter; CAN FD bila adapter melaporkannya |
| `pcan:usb1` … `pcan:usb16` | `PcanCanBus` | semua | PEAK PCAN-USB lewat PCANBasic (perlu paket driver); CAN klasik pada bit rate standar |
| `virtual:nama` | `VirtualCanBus` | semua | di dalam proses; setiap node dengan nama yang sama melihat frame node lain |

`gsusb:` dan `pcan:` ada di `IoTCom.Net.Transport.Can.Adapters` (bagian dari meta-package); keduanya mendaftarkan diri ke
`CanBus.Create` saat paket dimuat, atau secara eksplisit dengan `CanAdapters.Register()`. Kvaser dan Vector ada di
[roadmap](../../../PLAN.md).

## Instalasi

```bash
dotnet add package IoTCom.Net.Transport.Can --prerelease    # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Transport.Can;

await using var bus = await CanBus.OpenAsync("socketcan:can0");
using var reader = bus.OpenReader(new CanFilter(0x7E8, 0x7F8));     // 0x7E8–0x7EF
await bus.SendAsync(CanFrame.Parse("7DF#02010C"));                  // OBD-II: putaran mesin
CanFrame answer = await reader.ReadAsync();
Console.WriteLine(answer);                                           // 7E8#04410C1AF8
```

## Frame

`CanFrame` adalah struct immutable: `Id`, `Data`, `Flags` (`Extended`, `Remote`, `Fd`, `BitRateSwitch`,
`ErrorStateIndicator`, `Error`), `Dlc`, dan `Timestamp`. Konstruktornya memvalidasi lebar identifier dan panjang
data. Bentuk teks memakai notasi can-utils:

| Teks | Arti |
|---|---|
| `123#DEADBEEF` | id 11-bit 0x123, 4 byte |
| `18DAF110#0322F190` | id 29-bit |
| `123#R` / `123#R4` | remote frame (panjang 4) |
| `123##1001122…` | CAN FD; digit setelah `##` adalah flag (1 = BRS, 2 = ESI) |

`CanDlc.Pad(data)` menambal data hingga panjang CAN FD valid berikutnya (12, 16, 20, 24, 32, 48, 64).

## Menerima

- `OpenReader(filter)` mengembalikan `CanReader` berbuffer: `ReadAsync`, `ReadAsync(timeout)`, `TryRead`,
  `ReadAllAsync`. Setiap reader punya antrean sendiri. Saat antrean penuh, frame tertuanya dibuang dan dihitung di
  `Dropped`.
- `FrameReceived` dipicu di thread penerima untuk setiap frame. Jaga agar handler tetap singkat.
- `CanFilter.Exact(id)` cocok dengan satu identifier dengan lebar yang disimpulkan. `new CanFilter(id, mask)` cocok
  dengan sebuah rentang.

## Konfigurasi (`CanBusOptions`)

| Opsi | Bawaan | Keterangan |
|---|---|---|
| `Bitrate` | 500 000 | bit rate nominal (slcan; untuk SocketCAN atur dengan `ip link`) |
| `DataBitrate` | 2 000 000 | fase data CAN FD (perintah slcan `Y`) |
| `Fd` | false | aktifkan frame CAN FD |
| `ListenOnly` | false | buka dalam mode diam; `SendAsync` melempar error |
| `ReceiveOwnMessages` | false | kirim juga frame milik sendiri ke reader sendiri |
| `ReaderCapacity` | 4096 | jumlah frame yang dibuffer per reader |

## Menyiapkan perangkat keras

```bash
# Linux, kontroler bawaan atau adapter USB dengan driver SocketCAN (gs_usb, PCAN, Kvaser):
sudo ip link set can0 up type can bitrate 500000
# antarmuka virtual untuk pengujian:
sudo modprobe vcan && sudo ip link add vcan0 type vcan && sudo ip link set vcan0 up
```

Adapter slcan tidak butuh driver di OS mana pun. Cukup berikan port serial dan bit rate, dan `SlcanBus` mengirim
`S6`/`O` sendiri.

Adapter candleLight (`gsusb:`) terenumerasi dengan driver WinUSB di Windows, sehingga berfungsi tanpa memasang apa pun; di
Linux pakai driver kernel `gs_usb` lewat `socketcan:`, atau lepaskan driver itu dan tambahkan aturan udev untuk `1d50:606f`
agar `gsusb:` bisa dipakai dari user space. Adapter PEAK (`pcan:`) memerlukan paket driver PEAK: PCANBasic di Windows,
driver PCAN dengan `libpcanbasic` di Linux, atau PCBUSB di macOS.

## Simulator dan alat

```bash
iotcom can list                                   # antarmuka SocketCAN dan port serial
iotcom can dump --can socketcan:can0 --filter 7E8:7F8
iotcom can send --can slcan:COM5 7DF#02010C
iotcom can simulate --port 20100                  # simulator ECU di balik adapter slcan tiruan lewat TCP
iotcom can dump --can slcan-tcp:127.0.0.1:20100
```

`SlcanAdapterSimulator` meniru adapter slcan di stream byte apa pun, dan `VirtualCanNetwork` menghubungkan node di
memori. Keduanya membuat jalur slcan bisa diuji ujung ke ujung dalam unit test.

## Pengujian dan interoperabilitas

Pengujian mencakup notasi can-utils, tabel DLC, filter, fan-out di bus virtual, codec slcan (termasuk CAN FD dan
akhiran timestamp), sesi slcan lengkap lewat adapter tiruan, serta tata letak `can_frame`/`canfd_frame` SocketCAN. Uji adapter memeriksa frame host gs_usb terhadap tata letak
driver Linux dan bit timing untuk candleLight 48 MHz (500 kbit/s → prescaler 6, 16 quanta, 87,5 %). Uji itu juga
menjalankan sesi gs_usb penuh lewat candleLight virtual yang dijembatani ke jaringan CAN virtual, serta memeriksa
handle kanal PCAN dan image `TPCANMsg`.
CI juga menjalankan uji `vcan0` langsung di Linux bila modul kernelnya tersedia.

## Keamanan

CAN tidak punya autentikasi: node mana pun bisa mengirim identifier apa pun. Mengirim frame ke kendaraan atau mesin
yang sedang berjalan dapat memicu perilaku berbahaya. Pakai `ListenOnly` untuk pemantauan, dan kirim hanya di
meja uji atau saat peralatan dalam kondisi aman.

## Keterbatasan

Belum ada backend Kvaser atau Vector, dan `pcan:` hanya CAN klasik (kanal CAN FD memerlukan `CAN_InitializeFD`). Error frame muncul
sebagai frame bertanda, tetapi pemulihan bus-off diserahkan ke driver. CAN XL tidak didukung.

## Pelajari lebih lanjut

[ISO-TP, UDS, dan OBD-II](uds.md) · demo Galeri *Diagnostik kendaraan* · notebook
`notebooks/automotive/06-can-uds.id.ipynb` · `iotcom can --help`
