---
title: ISO-TP, UDS and OBD-II
translation-status: synced
---

# ISO-TP, UDS and OBD-II

**Summary.** Vehicle diagnostics run on top of CAN in three layers:

- **ISO-TP** (ISO 15765-2) carries messages longer than one frame: up to 4095 bytes, or 4 GiB with CAN FD.
- **UDS** (ISO 14229) is the diagnostic protocol of every modern ECU: sessions, security access, data identifiers,
  trouble codes, routines and flashing.
- **OBD-II** (SAE J1979) is the legislated subset that every car since 2001 (EU) or 1996 (US) answers: live data,
  emission-related trouble codes and the VIN.

IoTCom.Net implements all three: ISO-TP as a fuzzed **Rust** state machine, and UDS, OBD-II and an **ECU simulator**
in C#.

![Vehicle diagnostics](../../images/gallery-can-uds.png)

> **Safety.** Only diagnose vehicles you own or are authorised to service. Clearing codes, resetting ECUs and
> writing data change the vehicle. The tester has a read-only mode, and the CLI requires `--allow-write`.

## Packages

| Package | Contents |
|---|---|
| `IoTCom.Net.Protocols.IsoTp` | `IsoTpChannel` over any `ICanBus`; native `iotcom_isotp` for 9 RIDs |
| `IoTCom.Net.Protocols.Uds` | `UdsClient`, `ObdClient`, `EcuSimulator`, `Dtc`, `UdsAnatomy` |

```bash
dotnet add package IoTCom.Net.Protocols.Uds --prerelease
```

## ISO-TP

```csharp
await using var tp = IsoTpChannel.Create(bus, o => { o.TxId = 0x7E0; o.RxId = 0x7E8; });
await tp.ConnectAsync();
await tp.SendAsync(new byte[] { 0x22, 0xF1, 0x90 });  // single frame
byte[] vin = await tp.ReceiveAsync();                  // first frame + flow control + consecutive frames
```

The Rust machine handles single, first, consecutive and flow-control frames, block size, STmin (including the
100–900 µs codes), FC.WAIT, overflow, sequence errors, the `N_Bs` and `N_Cr` timeouts, padding, extended addressing
and CAN FD (escape lengths, 32-bit `FF_DL`). Failures surface as an `IsoTpException` with an `IsoTpError`.
Fuzz targets run nightly in CI.

| Option | Default | Description |
|---|---|---|
| `TxId` / `RxId` | — | identifiers (29-bit when > 0x7FF or `ExtendedIds = true`) |
| `Fd`, `TxDataLength` | false, 8 (64 with FD) | CAN FD frames |
| `Padding` | 0xCC | fill byte; null sends the shortest frames |
| `BlockSize`, `SeparationTime` | 0, 0 | what we announce in our flow control |
| `FlowControlTimeout`, `ConsecutiveFrameTimeout` | 1 s | N_Bs, N_Cr |
| `MaxMessageLength` | 4095 | larger first frames get FC.OVFLW |
| `TxAddressExtension` / `RxAddressExtension` | — | extended/mixed addressing byte |

## UDS tester

```csharp
await using var uds = UdsClient.Create(bus, o => { o.RequestId = 0x7E0; o.ResponseId = 0x7E8; });
await uds.ConnectAsync();
string vin = await uds.ReadVinAsync();                          // 22 F190
var dtcs = await uds.ReadDtcsAsync(DtcStatus.Confirmed);        // 19 02 08
await uds.StartSessionAsync(UdsSession.Extended);               // 10 03
await uds.SecurityAccessAsync(0x01, seed => MyOemKey(seed));    // 27 01 / 27 02
await uds.WriteDataByIdentifierAsync(0xF198, "WS-01"u8);        // 2E F198
```

- **Response handling:** NRC 0x78 (response pending) switches to the P2* timeout. NRC 0x21 (busy) is retried. With
  a suppressed positive response (sub-function bit 7, e.g. `TesterPresentAsync`) the client returns null. Other
  negative responses throw `UdsNegativeResponseException` with `ResponseCode`.
- **Read-only mode:** `ReadOnly = true` (or `AsReadOnly()`) blocks the state-changing services: reset, clear DTCs,
  write DID, routines, download, communication control and DTC setting.
- **Raw requests:** `RequestAsync(bytes)` sends any request. `UdsAnatomy.Describe(bytes)` gives the frame lane.

## OBD-II scan tool

```csharp
await using var obd = ObdClient.Create(bus);                    // functional 0x7DF → 0x7E8
await obd.ConnectAsync();
var rpm = await obd.ReadPidAsync(ObdPids.EngineRpm);            // 01 0C
string vin = await obd.ReadVinAsync();                          // 09 02 (multi-frame)
var stored = await obd.ReadDtcsAsync();                         // 03
var pending = await obd.ReadPendingDtcsAsync();                 // 07
```

`ObdPids` decodes (and, for simulators, encodes) the common mode 01 PIDs: load, coolant, MAP, rpm, speed, intake
temperature, MAF, throttle, run time, fuel level, voltage, ambient and oil temperature, and fuel rate.
`GetSupportedPidsAsync()` reads the support bitmaps. Flow control for multi-frame answers goes to the ECU's
physical identifier (0x7E0), as ISO 15765-4 requires.

## Trouble codes

`Dtc` holds the 3-byte UDS code and the status bits (`DtcStatus`: test failed, pending, confirmed, MIL…) and prints
the SAE form: `P0301`, `U0100-87` (with failure type), `C1234`, `B0001`. Use `Dtc.Parse("P0420")` and
`Dtc.FromObd(0x0420)`.

## ECU simulator

`EcuSimulator` answers on 0x7E0/0x7E8 (physical) and 0x7DF (functional), with a vehicle model running a 60-second
urban drive cycle:

- **OBD-II:** modes 01 (14 PIDs and support bitmaps), 03, 04, 07, and 09 (VIN).
- **UDS:** sessions with P2/P2* timings and an S3 timeout; ECU reset; TesterPresent; ReadDataByIdentifier (F190,
  F187, F189, F18C, F197, F186, F198 and the live record 0100); WriteDataByIdentifier on F198 (extended session plus
  security access); security access level 1 with `EcuSimulator.ComputeKey` (a demo algorithm), attempt counting
  and a lock-out delay; ReadDTCInformation 01/02/0A; ClearDiagnosticInformation; and RoutineControl 0203 (answers
  "response pending" first).
- **Fault injection:** DTCs P0301 (confirmed, MIL) and P0420 (pending) are preset. `Vehicle.CoolingFault` makes the
  coolant climb until P0217 is set.

```bash
iotcom uds read --can sim                   # built-in simulator, no hardware
iotcom uds dtc --can socketcan:can0
iotcom uds raw --can sim 1003
iotcom obd live --can slcan:COM5 --watch 500
iotcom obd dtc --can sim --clear --allow-write
dotnet run --project samples/console/UdsTester -- socketcan:can0
```

## Testing and interoperability

- **Rust:** unit tests cover a real VIN multi-frame exchange, block size and STmin pacing, CAN FD with the 32-bit
  length escape, timeouts, overflow, sequence errors, WAIT frames and extended addressing. Fuzz targets run on
  adversarial input.
- **.NET:** tests run the full stack on a virtual bus: identification, DTC status masks, security access (right and
  wrong key), session and security guards on writes, response-pending routines, suppressed responses, read-only
  mode, and the OBD PIDs, VIN and DTC modes.
- **Over a real stream:** the CLI talks through an emulated slcan adapter over TCP.

## Limitations

Not yet included: flashing helpers (`RequestDownload` / `TransferData` sequences are possible with `RequestAsync`),
J1939, DoIP (UDS over Ethernet) and OEM-specific seed/key algorithms, which you supply as a delegate. OBD-II covers
CAN vehicles (ISO 15765-4); K-line and J1850 are out of scope.

## Learn more

[CAN and CAN FD](can.md) · Gallery demo *Vehicle diagnostics* · notebook `notebooks/automotive/06-can-uds.en.ipynb` ·
`iotcom uds --help`, `iotcom obd --help`
