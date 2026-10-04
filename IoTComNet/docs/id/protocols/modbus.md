---
title: Modbus TCP / RTU / ASCII
translation-status: synced
---

# Modbus TCP / RTU / ASCII

**Ringkasan.** Protokol request/response untuk PLC, meter energi, drive, modul I/O, dan sensor. IoTCom.Net memberi
Anda master (client) dan slave (server) dalam satu API, simulator bawaan, serta mesin protokol Rust opsional.

## Kapan dipakai

- Membaca atau menulis PLC, meter, VFD, inverter, atau remote I/O di LAN atau bus RS-485.
- Mensimulasikan perangkat untuk pengembangan HMI/SCADA atau pengujian otomatis.
- Membangun gateway (Modbus → MQTT/HTTP) — lihat [sampel gateway](../guides/gateway.md).

## Peran

| Peran | Tipe | Catatan |
|---|---|---|
| Master / client | `ModbusClient` | pipelining TCP, serialisasi RTU/ASCII, retry, reconnect, mode read-only |
| Master (mesin Rust) | `NativeModbusClient` | API `IModbusClient` yang sama, state machine sans-I/O Rust |
| Slave / server | `ModbusServer` | banyak client TCP, filter unit id, broadcast, identifikasi perangkat |
| Simulator | `ModbusSimulator` | menganimasikan `ModbusDataStore`; peta siap pakai `CreateVirtualPlc()` |

Fungsi: 0x01–0x06, 0x0F, 0x10, 0x16 (mask write), 0x17 (read/write), 0x2B/0x0E (identifikasi perangkat), PDU mentah lewat `SendAsync`.

## Transport

| Varian | Client | Server |
|---|---|---|
| Modbus TCP | `UseTcp(host, 502)` | `UseTcp(IPAddress.Any, 502)` |
| RTU di RS-485/232 | `UseSerial(port, baud, Parity.Even).UseRtuFraming()` | `ServeSerial(port, baud).UseRtuFraming()` |
| RTU over TCP | `UseTcp(host, port).UseRtuFraming()` | `UseTcp(...).UseRtuFraming()` |
| ASCII | `.UseAsciiFraming()` | `.UseAsciiFraming()` |
| Dalam proses | `UseInMemory(listener)` | `ListenInMemory(listener)` |

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Modbus --prerelease
dotnet add package IoTCom.Net.Transport.Serial --prerelease   # untuk RTU di port serial
dotnet add package IoTCom.Net.Native.Modbus --prerelease      # mesin Rust opsional
```

## Mulai cepat

```csharp
await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithUnitId(1));
ushort[] regs = await plc.ReadHoldingRegistersAsync(address: 0, count: 10);
float flow = await plc.ReadSingleAsync(100, ModbusWordOrder.WordSwap);
await plc.WriteSingleRegisterAsync(20, 1500);
```

## Konfigurasi

| Opsi | Default | Deskripsi |
|---|---|---|
| `WithUnitId(byte)` | 1 | unit id default (bisa diganti per panggilan dengan `unitId:`) |
| `WithTimeout(TimeSpan)` | 1 dtk | timeout response |
| `WithRetries(int)` | 0 | jumlah retry setelah timeout |
| `AsReadOnly()` | mati | setiap penulisan melempar `ReadOnlyModeException` sebelum mencapai jalur |
| `WithMaxConcurrentRequests(int)` | 16 | request yang sedang berjalan di Modbus TCP (RTU/ASCII selalu 1) |
| `WithReconnect(ReconnectPolicy)` | backoff 0,5 dtk → 30 dtk | reconnect setelah link terputus |
| `WithTap(ITrafficTap)` | — | menangkap frame |
| Server `WithUnitIds(...)` | semua | hanya menjawab unit ini (TCP: unit lain mendapat exception 0x0B) |
| Server `WithStore(store)` | store baru | berbagi data dengan simulator atau aplikasi Anda |
| Server `AsReadOnly()` | mati | penulisan dijawab IllegalFunction |

Alamat adalah alamat protokol **berbasis 0**: dokumentasi yang menulis "40001" berarti holding register 0.

## Contoh

```csharp
// Nilai multi-register: ada empat urutan word di lapangan.
var regs = await plc.ReadHoldingRegistersAsync(0, 4);
double energy = ModbusConvert.ToDouble(regs, ModbusWordOrder.BigEndian);

// Identifikasi perangkat (0x2B / 0x0E)
var id = await plc.ReadDeviceIdentificationAsync();

// Slave yang bereaksi terhadap penulisan
await using var slave = ModbusServer.Create(o => o.UseTcp(IPAddress.Any, 1502));
slave.Store.HoldingRegisters.Changed += (_, e) => { if (e.FromRemote) Console.WriteLine($"HR{e.Address} ditulis"); };
await slave.StartAsync();

// Ganti ke mesin Rust — interface sama
IModbusClient fast = NativeModbusClient.Create(o => o.UseTcp("192.168.1.10", 502));
```

## Simulator

Peta `ModbusSimulator.CreateVirtualPlc(store)`: IR0 suhu×10, IR1 kelembapan×10, IR2 tekanan, IR3 rpm, IR4-5 daya kW
(float32), IR6-7 energi kWh (float32), HR0 setpoint×10 (rw), HR1 penghitung produksi, HR2 word alarm, HR10-21 nama
perangkat, coil 0 motor (rw), coil 1 pompa pendingin, DI0 pintu, DI1 e-stop, DI2 suhu tinggi. Tambahkan sinyal
sendiri: `simulator.AddSignal(ModbusTable.HoldingRegisters, 50, t => Math.Sin(t) * 100)`. Jalankan secara
deterministik dengan `Tick(detik)` di pengujian.

```bash
iotcom modbus serve --port 1502 --simulate
```

## Pengujian dan interoperabilitas

Vector bersama di `/conformance/modbus.json` (contoh dari spesifikasi Modbus Application Protocol dan Serial Line)
dijalankan di suite C# dan Rust, dan uji lintas bahasa memastikan mesin Rust dan framing managed menghasilkan byte
yang identik. Uji integrasi mencakup socket TCP, pipelining, reconnect setelah server dinyalakan ulang, filter unit
RTU, dan response exception.

## Keamanan dan keselamatan

Modbus tidak punya autentikasi atau enkripsi. Tempatkan di jaringan terisolasi, jangan pernah membuka port 502 ke
internet, dan pasang gateway untuk akses jarak jauh. Penulisan menggerakkan peralatan sungguhan: gunakan
`AsReadOnly()` saat commissioning; CLI mewajibkan `--allow-write` plus konfirmasi.

## Keterbatasan

- Timing RTU mengandalkan aturan panjang frame, bukan jeda 3,5 karakter yang tidak dapat diamati secara andal di OS
  desktop. Kode fungsi yang tidak dikenal pada RTU diresinkronisasi byte per byte.
- Kontrol arah RS-485 diharapkan dilakukan adapter (auto-direction).
- Diagnostik (0x08) dan file record (0x14/0x15) bisa diakses lewat `SendAsync` tetapi belum punya helper bertipe.

## Pelajari lebih lanjut

Notebook `notebooks/industrial/01-modbus.id.ipynb` · demo Galeri *PLC pabrik pintar* · `iotcom modbus --help`
