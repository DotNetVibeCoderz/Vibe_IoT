---
title: CANopen
translation-status: synced
---

# CANopen (CiA 301)

**Ringkasan.** CANopen memberi jaringan CAN sebuah model perangkat. Drive, modul I/O, encoder, sensor, sistem baterai,
dan perangkat medis membuka object dictionary yang dialamatkan dengan index dan sub-index. Master mengonfigurasi dan
membacanya dengan SDO, bertukar data proses lewat PDO, mengatur state node dengan NMT, dan mengawasinya dengan
heartbeat. `IoTCom.Net.Protocols.CanOpen` berjalan di `ICanBus` apa pun (SocketCAN, slcan, gs_usb, PCAN, virtual)
dan menyediakan:

- `CanOpenMaster`:
  - perintah NMT dan konsumen heartbeat (`NodeStateChanged`)
  - client SDO: expedited dan bersegmen, `ReadAsync`/`WriteAsync` bertipe, `CanOpenSdoException` dengan kode abort CiA
  - SYNC, event `PdoReceived` / `EmergencyReceived`, `ReadTpdoMappingAsync`, dan `ScanAsync` (tipe perangkat, nama,
    dan identitas setiap node yang menjawab)
  - mode read-only yang menolak unduhan SDO dan NMT
- `CanOpenNode`: sebuah perangkat.
  - boot-up dan state machine NMT, produsen heartbeat (0x1017)
  - server SDO dengan abort untuk objek yang tidak ada, akses read-only/write-only, dan panjang yang tidak cocok
  - TPDO (digerakkan event dengan event timer, atau setiap SYNC ke-n) dan RPDO yang ditulis ke dictionary
  - emergency
- `ObjectDictionary` dengan objek komunikasi standar, `DefinePdo`, dan `PdoMapping`.
- `CanOpenCodec`: COB-ID, frame SDO, dan frame lane.
- `CanOpenIoModuleSimulator`: modul I/O bergaya CiA 401 (8 DI, 8 DO, 2 AI, suhu).

Codec-nya punya kembaran Rust yang di-fuzz (`rust/crates/iotcom-canopen`) dan diperiksa terhadap vektor
`/conformance/canopen.json` yang sama, yang berasal dari referensi Python independen.

## Kapan dipakai

- Commissioning dan pemantauan perangkat CANopen dari PC, gateway, atau meja uji.
- Menjembatani data proses CANopen ke MQTT, OPC UA, atau basis data.
- Membangun perangkat CANopen di .NET, atau menyimulasikannya untuk menguji PLC atau HMI.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.CanOpen --prerelease    # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.CanOpen;
using IoTCom.Net.Transport.Can;

await using var bus = await CanBus.OpenAsync("socketcan:can0");          // atau slcan:COM5, gsusb:, pcan:usb1
await using var master = CanOpenMaster.Create(bus, o => o.ReadOnly = true);
await master.StartAsync();

foreach (var n in await master.ScanAsync()) Console.WriteLine($"{n.Id}: {n.Name} vendor 0x{n.Identity?.Vendor:X8}");
var name = await master.ReadAsync(5, 0x1008, 0, CanOpenDataType.VisibleString);   // bersegmen bila > 4 byte
var map = await master.ReadTpdoMappingAsync(5, 1);
master.PdoReceived += (node, pdo, data) => Console.WriteLine(string.Join(" ", map.Unpack(data).Select(v => $"{v.Object}={Convert.ToHexString(v.Raw)}")));
```

Sebuah perangkat:

```csharp
var od = ObjectDictionary.CreateStandard(0x000F0191, "My IO", vendorId: 0xABC, productCode: 1, revision: 1, serial: 42);
od.Add(0x6000, 1, "Inputs", CanOpenDataType.Unsigned8, CanOpenAccess.ReadOnly, (byte)0, pdoMappable: true);
od.DefinePdo(transmit: true, 1, CanOpenCodec.TpdoCobId(1, 10), [(0x6000, 1)], transmissionType: 0xFF, eventTimerMs: 100);
await using var node = CanOpenNode.Create(bus, nodeId: 10, od);
await node.StartAsync();                    // boot-up, pre-operational; NMT start dari master membuatnya mengirim PDO
od.Set(0x6000, 1, (byte)0b101);             // TPDO yang digerakkan event terkirim
```

## Keamanan

NMT dan unduhan SDO mengubah apa yang dilakukan mesin. `ReadOnly = true` membuat `DownloadAsync`, `WriteAsync`, dan
`NmtAsync` melempar `ReadOnlyModeException`. `iotcom canopen write` dan `iotcom canopen nmt` perlu `--allow-write`, dan
`write` meminta konfirmasi di bus sungguhan.

## Alat

```bash
iotcom canopen scan --can sim                       # dua modul I/O simulasi (node 5 dan 6)
iotcom canopen read 5 1008 -t str --can sim
iotcom canopen write 5 6200:01 1 -t u8 --can sim --allow-write
iotcom canopen nmt start 0 --can socketcan:can0 --allow-write
iotcom canopen monitor --can gsusb:                 # heartbeat, PDO, dan emergency
```

Ekstensi VS Code mengurai frame CANopen (`canopen`). Gallery: *Modul I/O CANopen*.

## Pengujian

Uji codec memakai contoh byte CiA 301: pembacaan expedited 1 hingga 4 byte, penulisan producer heartbeat, abort, dan
upload bersegmen dengan segmen bertoggle. Uji itu juga mencakup klasifikasi COB-ID untuk setiap fungsi dan nomor
PDO, entri mapping PDO, dan EMCY. Uji sesi menjalankan master dan modul simulasi di jaringan virtual:
- pemindaian dengan identitas, SDO bersegmen di kedua arah, dan setiap abort yang dihasilkan server;
- node yang tidak menjawab, dan node stopped yang mengabaikan SDO;
- transisi NMT, TPDO lewat event timer dan pada SYNC, RPDO yang menyalakan output;
- emergency, reset ke pre-operational, dan master read-only.

71 vektor bersama berjalan di rangkaian uji C# dan Rust. Codec Rust di-fuzz sebanyak 7 juta run tanpa temuan.

## Keterbatasan

Belum ada SDO block transfer (diurai sebagai tidak didukung), LSS, node guarding, produsen time stamp, atau impor
EDS/DCF. Mapping PDO selaras byte. Timeout konsumen heartbeat dilaporkan lewat `LastSeen`, bukan event.

## Pelajari lebih lanjut

Notebook `notebooks/industrial/16-canopen.id.ipynb` · sampel `samples/console/CanOpenMaster` · [CAN / CAN FD](can.md)
