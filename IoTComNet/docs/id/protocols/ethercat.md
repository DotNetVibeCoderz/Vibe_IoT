---
title: EtherCAT
translation-status: synced
---

# Master EtherCAT

**Ringkasan.** EtherCAT adalah Ethernet untuk I/O fieldbus: master mengirim frame biasa (EtherType 0x88A4) dan setiap
slave membaca dan menulis bagiannya saat frame lewat, di perangkat keras, lalu meneruskannya; slave terakhir
mengirimnya kembali. Satu frame melayani seluruh jalur, dan working counter setiap datagram memberi tahu master berapa
slave yang melayaninya. `IoTCom.Net.Protocols.EtherCat` menyediakan:

- Codec (sans-I/O): `EcatFrame` (header Ethernet + EtherCAT, datagram dengan alamat ADP/ADO atau logis, flag more dan
  circulating, working counter, frame lane), `EcatMailbox`, `CoeSdo` (transfer expedited dan normal, abort), `EcatSii`
  (identitas, konfigurasi mailbox, kategori strings, general, SyncManager, dan PDO), `EcatAlStatus`.
- `EthercatMaster`: pemindaian jalur (hitungan broadcast, alamat stasiun 0x1001…, identitas SII dan ukuran process
  data), state machine AL INIT → PRE-OP → SAFE-OP → OP dengan SyncManager mailbox, SyncManager process data, dan FMMU
  yang dipetakan ke satu image logis, pertukaran siklik (`ExchangeAsync`, `RunAsync`) dengan pemeriksaan working
  counter dan statistik, akses objek CoE (`SdoReadAsync`/`SdoWriteAsync`), pembacaan register, dan pengakuan error.
  **Hanya-baca secara bawaan**: tanpa `AllowWrites()` jalur berhenti di SAFE-OP (input hidup, output aman) dan
  pertukarannya berupa pembacaan logis.
- Link: `RawEthernetLink` — socket AF_PACKET di Linux, Npcap di Windows, libpcap di macOS — dan
  `VirtualEthercatSegment`, jalur ESC simulasi (memori register, state machine AL, EEPROM SII, SyncManager, FMMU,
  mailbox CoE) dengan kabel yang bisa diputus setelah slave mana pun dan laju kehilangan frame.
- `EthercatRackSimulator`: stasiun konveyor dengan coupler, terminal input digital 8 kanal, output digital 8 kanal,
  dan input analog 4 kanal.

Sesuai desain (tingkat kurasi 2, kandidat Rust `ethercrab`), master ditulis dalam C# terkelola dengan kembaran codec
Rust, sama seperti EtherNet/IP: membungkus ethercrab akan membutuhkan executor async di balik FFI dan tidak bisa
menggerakkan segmen dalam memori lewat codec yang sama. Crate Rust `iotcom-ethercat` di-fuzz dan berbagi 37 vektor di
`/conformance/ethercat.json` dengan suite C#.

## Kapan dipakai

- Membaca dan menggerakkan I/O EtherCAT dari .NET pada beberapa ratus hertz: test bench, tester akhir lini, rig lab,
  akuisisi data, alat komisioning.
- Memeriksa sebuah jalur: siapa saja di dalamnya, di state apa, dengan identitas dan objek apa.
- Mengajar dan menguji tanpa perangkat keras (`VirtualEthercatSegment`, `iotcom ethercat … --sim`).

Ini **bukan** pengganti master EtherCAT real-time di motion controller: siklus berjalan di scheduler .NET dan stack
jaringan sistem operasi, jadi harapkan jitter di kisaran 0,1–1 ms (lebih besar di Windows yang resolusi timernya kasar).
Distributed clock tidak dikonfigurasi.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.EtherCat --prerelease    # juga bagian dari meta-package IoTCom.Net
```

Ethernet mentah membutuhkan hak istimewa: di Linux `sudo setcap cap_net_raw+ep <executable>` (atau root); di Windows
pasang [Npcap](https://npcap.com) dengan "WinPcap API-compatible mode" dan jalankan sebagai administrator; di macOS
jalankan sebagai root atau beri akses ke `/dev/bpf*`. Gunakan port jaringan khusus untuk EtherCAT.

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.EtherCat;

await using var master = EthercatMaster.Create(o => o.UseInterface("eth1"));   // hanya-baca
foreach (var slave in await master.ScanAsync()) Console.WriteLine(slave);
await master.RequestStateAsync(EcatState.SafeOp);                             // input hidup, output aman
await master.ExchangeAsync();
Console.WriteLine(Convert.ToHexString(master.InputsOf(master.Slaves[1])));
var name = await master.SdoReadAsync(master.Slaves[3], 0x1008, 0);            // nama perangkat CoE
```

Menggerakkan output, setelah diizinkan:

```csharp
await using var master = EthercatMaster.Create(o => o.UseInterface("eth1").AllowWrites());
await master.ScanAsync();
await master.RequestStateAsync(EcatState.Op);
master.SetOutputBit(master.Slaves[2], 0, true);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await master.RunAsync(TimeSpan.FromMilliseconds(4), stop.Token);
Console.WriteLine(master.Statistics);   // siklus, working counter kurang, frame hilang, round trip
```

Hosting: `services.AddIoTCom(b => b.AddEthercatMaster("line", o => o.UseInterface("eth1")))` membuka link bersama host
dan mengembalikan jalur ke PRE-OP saat host berhenti.

## Keselamatan

Output menggerakkan mesin. Master menolak OP, `SetOutputs`, `SetOutputBit`, dan download SDO sampai `AllowWrites()`;
CLI membutuhkan `--allow-write` dan meminta konfirmasi sebelum menggerakkan jalur sungguhan, dan Gallery punya kunci
tulis. Jangan pernah mengandalkan pustaka ini untuk fungsi keselamatan: tombol darurat, pengaman, dan light curtain
tempatnya di perangkat keras ber-rating keselamatan (FSoE/Safety over EtherCAT tidak diimplementasikan). Menutup master
(atau menghentikan host) mengembalikan jalur ke PRE-OP, tempat slave menahan output dalam keadaan aman; slave yang
watchdog SyncManager-nya habis melakukan hal yang sama dengan sendirinya.

## Alat

```bash
iotcom ethercat interfaces                                  # perangkat AF_PACKET atau Npcap/libpcap
iotcom ethercat scan --if eth1                              # atau --sim
iotcom ethercat watch --sim                                 # SAFE-OP, input dan working counter langsung
iotcom ethercat sdo 3 1008:00 --sim                         # upload CoE
iotcom ethercat sdo 3 8000:15 --write 0200 --sim --allow-write
iotcom ethercat set 2 0 on --sim --allow-write              # OP beberapa detik dengan satu output menyala
iotcom ethercat demo [--allow-write]
iotcom ethercat decode "FFFFFFFFFFFF010101010101 88A4 0D10 0701 00000000 0100 0000 00 0400"
```

Capture menulis frame EtherCAT sebagai Ethernet (LINKTYPE_ETHERNET), sehingga dissector EtherCAT Wireshark
menguraikannya; ekstensi VS Code mengurai `ethercat`. Gallery: *Rak I/O lewat EtherCAT*.

## Pengujian

Tes codec mencakup padding dan flag more, frame rusak, mailbox dan SDO, serta image SII; 37 vektor bersama mencakup
payload datagram (broadcast, posisional, terkonfigurasi, logis, circulating), frame Ethernet dan flag returned,
mailbox, SDO expedited dan normal beserta abort, image SII, dan masukan rusak dari setiap jenis; kembaran Rust di-fuzz
(`ethercat`, 3 juta run lokal). Tes master berjalan terhadap rak simulasi: pemindaian, SAFE-OP hanya-baca dengan input,
penolakan, OP yang menggerakkan konveyor dan tombol darurat, baca/tulis CoE dan abort, kabel putus pada working counter,
frame hilang dengan percobaan ulang, error AL dengan kode status dan pengakuannya, traffic tap, dan hosting.

## Batasan

Distributed clock, EoE/FoE/SoE, transfer SDO bersegmen, SDO information, pemetaan ulang PDO lewat CoE (PDO dari SII
dipakai apa adanya), redundansi (cincin kabel), hot connect, alias, dan process image yang lebih besar dari satu frame
(sekitar 1,4 KB) belum diimplementasikan. Process data rata per byte untuk setiap slave.

## Pelajari lebih lanjut

Notebook `notebooks/industrial/29-ethercat.id.ipynb` · sampel `samples/console/EthercatRack` ·
[EtherNet/IP](ethernetip.md) · [CANopen](canopen.md) (CoE memakai model object dictionary yang sama)
