---
title: ISO-TP, UDS, dan OBD-II
translation-status: synced
---

# ISO-TP, UDS, dan OBD-II

**Ringkasan.** Diagnostik kendaraan berjalan di atas CAN dalam tiga lapisan:

- **ISO-TP** (ISO 15765-2) membawa pesan yang lebih panjang dari satu frame: hingga 4095 byte, atau 4 GiB dengan
  CAN FD.
- **UDS** (ISO 14229) adalah protokol diagnostik setiap ECU modern: session, security access, data identifier, kode
  kerusakan, routine, dan flashing.
- **OBD-II** (SAE J1979) adalah bagian wajib yang dijawab setiap mobil sejak 2001 (UE) atau 1996 (AS): data langsung,
  kode kerusakan terkait emisi, dan VIN.

IoTCom.Net mengimplementasikan ketiganya: ISO-TP sebagai state machine **Rust** yang di-fuzz, serta UDS, OBD-II, dan
**simulator ECU** di C#.

![Diagnostik kendaraan](../../images/gallery-can-uds.png)

> **Keselamatan.** Diagnosis hanya kendaraan milik Anda atau yang Anda berwenang servis. Menghapus kode, mereset
> ECU, dan menulis data mengubah kendaraan. Tester punya mode read-only, dan CLI mensyaratkan `--allow-write`.

## Paket

| Paket | Isi |
|---|---|
| `IoTCom.Net.Protocols.IsoTp` | `IsoTpChannel` di atas `ICanBus` apa pun; native `iotcom_isotp` untuk 9 RID |
| `IoTCom.Net.Protocols.Uds` | `UdsClient`, `ObdClient`, `EcuSimulator`, `Dtc`, `UdsAnatomy` |

```bash
dotnet add package IoTCom.Net.Protocols.Uds --prerelease
```

## ISO-TP

```csharp
await using var tp = IsoTpChannel.Create(bus, o => { o.TxId = 0x7E0; o.RxId = 0x7E8; });
await tp.ConnectAsync();
await tp.SendAsync(new byte[] { 0x22, 0xF1, 0x90 });  // single frame
byte[] vin = await tp.ReceiveAsync();                  // first frame + flow control + consecutive frame
```

Mesin Rust menangani single, first, consecutive, dan flow-control frame, block size, STmin (termasuk kode
100–900 µs), FC.WAIT, overflow, kesalahan urutan, timeout `N_Bs` dan `N_Cr`, padding, extended addressing, dan
CAN FD (panjang escape, `FF_DL` 32-bit). Kegagalan muncul sebagai `IsoTpException` dengan `IsoTpError`. Target fuzz
berjalan tiap malam di CI.

| Opsi | Bawaan | Keterangan |
|---|---|---|
| `TxId` / `RxId` | — | identifier (29-bit bila > 0x7FF atau `ExtendedIds = true`) |
| `Fd`, `TxDataLength` | false, 8 (64 dengan FD) | frame CAN FD |
| `Padding` | 0xCC | byte pengisi; null mengirim frame terpendek |
| `BlockSize`, `SeparationTime` | 0, 0 | yang kita umumkan di flow control |
| `FlowControlTimeout`, `ConsecutiveFrameTimeout` | 1 dtk | N_Bs, N_Cr |
| `MaxMessageLength` | 4095 | first frame yang lebih besar dijawab FC.OVFLW |
| `TxAddressExtension` / `RxAddressExtension` | — | byte alamat extended/mixed |

## Tester UDS

```csharp
await using var uds = UdsClient.Create(bus, o => { o.RequestId = 0x7E0; o.ResponseId = 0x7E8; });
await uds.ConnectAsync();
string vin = await uds.ReadVinAsync();                          // 22 F190
var dtcs = await uds.ReadDtcsAsync(DtcStatus.Confirmed);        // 19 02 08
await uds.StartSessionAsync(UdsSession.Extended);               // 10 03
await uds.SecurityAccessAsync(0x01, seed => MyOemKey(seed));    // 27 01 / 27 02
await uds.WriteDataByIdentifierAsync(0xF198, "WS-01"u8);        // 2E F198
```

- **Penanganan respons:** NRC 0x78 (response pending) beralih ke timeout P2*. NRC 0x21 (busy) diulang. Untuk respons
  positif yang ditekan (bit 7 sub-function, mis. `TesterPresentAsync`) klien mengembalikan null. Respons negatif lain
  melempar `UdsNegativeResponseException` dengan `ResponseCode`.
- **Mode read-only:** `ReadOnly = true` (atau `AsReadOnly()`) memblokir layanan yang mengubah status: reset, hapus
  DTC, tulis DID, routine, download, communication control, dan DTC setting.
- **Request mentah:** `RequestAsync(bytes)` mengirim request apa pun. `UdsAnatomy.Describe(bytes)` memberi frame lane.

## Scan tool OBD-II

```csharp
await using var obd = ObdClient.Create(bus);                    // fungsional 0x7DF → 0x7E8
await obd.ConnectAsync();
var rpm = await obd.ReadPidAsync(ObdPids.EngineRpm);            // 01 0C
string vin = await obd.ReadVinAsync();                          // 09 02 (multi-frame)
var stored = await obd.ReadDtcsAsync();                         // 03
var pending = await obd.ReadPendingDtcsAsync();                 // 07
```

`ObdPids` mengurai (dan, untuk simulator, mengodekan) PID mode 01 yang umum: beban, pendingin, MAP, rpm, kecepatan,
suhu udara masuk, MAF, throttle, waktu jalan, level BBM, tegangan, suhu lingkungan dan oli, serta laju BBM.
`GetSupportedPidsAsync()` membaca bitmap dukungan. Flow control untuk jawaban multi-frame dikirim ke identifier
fisik ECU (0x7E0), sesuai ISO 15765-4.

## Kode kerusakan

`Dtc` menyimpan kode UDS 3 byte dan bit status (`DtcStatus`: test failed, pending, confirmed, MIL…) dan mencetak
bentuk SAE: `P0301`, `U0100-87` (dengan failure type), `C1234`, `B0001`. Gunakan `Dtc.Parse("P0420")` dan
`Dtc.FromObd(0x0420)`.

## Simulator ECU

`EcuSimulator` menjawab di 0x7E0/0x7E8 (fisik) dan 0x7DF (fungsional), dengan model kendaraan yang menjalankan
siklus berkendara kota 60 detik:

- **OBD-II:** mode 01 (14 PID dan bitmap dukungan), 03, 04, 07, dan 09 (VIN).
- **UDS:** session dengan timing P2/P2* dan timeout S3; ECU reset; TesterPresent; ReadDataByIdentifier (F190, F187,
  F189, F18C, F197, F186, F198, dan rekaman langsung 0100); WriteDataByIdentifier pada F198 (session extended plus
  security access); security access level 1 dengan `EcuSimulator.ComputeKey` (algoritma demo), penghitungan
  percobaan, dan jeda penguncian; ReadDTCInformation 01/02/0A; ClearDiagnosticInformation; serta RoutineControl 0203
  (menjawab "response pending" lebih dulu).
- **Injeksi gangguan:** DTC P0301 (confirmed, MIL) dan P0420 (pending) sudah tersedia. `Vehicle.CoolingFault`
  membuat suhu pendingin naik hingga P0217 tercatat.

```bash
iotcom uds read --can sim                   # simulator bawaan, tanpa perangkat keras
iotcom uds dtc --can socketcan:can0
iotcom uds raw --can sim 1003
iotcom obd live --can slcan:COM5 --watch 500
iotcom obd dtc --can sim --clear --allow-write
dotnet run --project samples/console/UdsTester -- socketcan:can0
```

## Pengujian dan interoperabilitas

- **Rust:** unit test mencakup pertukaran VIN multi-frame sungguhan, pacing block size dan STmin, CAN FD dengan
  escape panjang 32-bit, timeout, overflow, kesalahan urutan, frame WAIT, dan extended addressing. Target fuzz
  dijalankan dengan masukan adversarial.
- **.NET:** pengujian menjalankan seluruh tumpukan di bus virtual: identifikasi, mask status DTC, security access
  (key benar dan salah), penjagaan session dan security pada penulisan, routine dengan response pending, respons
  yang ditekan, mode read-only, serta PID, VIN, dan mode DTC OBD.
- **Lewat stream sungguhan:** CLI berbicara melalui adapter slcan tiruan lewat TCP.

## Keterbatasan

Belum tersedia: helper flashing (urutan `RequestDownload` / `TransferData` bisa dilakukan dengan `RequestAsync`),
J1939, DoIP (UDS lewat Ethernet), dan algoritma seed/key khusus OEM, yang Anda berikan sebagai delegate. OBD-II
mencakup kendaraan CAN (ISO 15765-4); K-line dan J1850 di luar cakupan.

## Pelajari lebih lanjut

[CAN dan CAN FD](can.md) · demo Galeri *Diagnostik kendaraan* · notebook `notebooks/automotive/06-can-uds.id.ipynb` ·
`iotcom uds --help`, `iotcom obd --help`
