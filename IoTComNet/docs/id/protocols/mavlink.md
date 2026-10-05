---
title: MAVLink
translation-status: synced
---

# MAVLink

**Ringkasan.** MAVLink adalah protokol telemetri dan perintah PX4, ArduPilot, dan sebagian besar drone, rover, kapal,
serta gimbal. IoTCom.Net menyediakan:

- **dialek common lengkap** (235 pesan), dihasilkan dari XML resmi oleh **source generator Roslyn** yang juga bisa
  Anda jalankan pada dialek Anda sendiri;
- codec frame v1/v2 dengan pemotongan MAVLink 2 dan **signing**;
- koneksi lewat UDP, TCP, atau serial;
- **helper ground station** (perintah ber-ACK, protokol parameter, mode read-only);
- **simulator quadcopter**.

![Telemetri drone lewat MAVLink](../../images/gallery-mavlink.png)

> **Keselamatan.** Perintah menggerakkan kendaraan sungguhan. Uji dulu dengan simulator atau SITL, lepas baling-baling
> di meja uji, dan pakai mode read-only untuk pemantauan. CLI mensyaratkan `--allow-write` untuk perintah dan
> perubahan parameter.

## Kapan dipakai

- Membangun ground station, dasbor, atau logger untuk kendaraan PX4/ArduPilot.
- Menjembatani telemetri ke MQTT, basis data, atau cloud dari companion computer.
- Menulis komponen MAVLink (kamera, sensor, payload) dengan pesan Anda sendiri.
- Menguji alat terhadap kendaraan simulasi tanpa SITL atau perangkat keras.

## Peran

| Peran | Tipe |
|---|---|
| Link (client / publisher / subscriber) | `MavlinkConnection`: `SendAsync`, `ReadAllAsync`, `WaitForAsync<T>`, `PacketReceived`, peer, statistik kehilangan, heartbeat |
| Ground station | `MavlinkGroundStation`: `CommandAsync`, `ArmAsync`, `TakeoffAsync`, `LandAsync`, `ReturnToLaunchAsync`, `ReadParametersAsync`, `SetParameterAsync`, `State`, `StatusText` |
| Simulator kendaraan | `MavlinkVehicleSimulator`: quadcopter mirip ArduCopter (aliran telemetri, perintah, pre-arm check, parameter) |
| Codec | `MavlinkCodec.Encode`, `MavlinkParser` (streaming, resync), `MavlinkSigning`, `MavlinkAnatomy` (sans-I/O) |
| Pesan | `IoTCom.Net.Protocols.Mavlink.Common`: `Heartbeat`, `Attitude`, `GlobalPositionInt`, `CommandLong`, … dan semua enum (`MavCmd`, `MavType`, …) |

## Transport

| Susunan | Kode |
|---|---|
| Ground station di UDP 14550 (menjawab pengirim terakhir) | `o.UseUdp(14550)` |
| Kendaraan / companion yang mengirim ke GCS | `o.UseUdp(14555).SendTo(new IPEndPoint(gcs, 14550))` |
| SITL atau MAVProxy lewat TCP | `o.UseTcp("127.0.0.1", 5760)` |
| Radio telemetri / USB flight controller | `o.UseSerial("COM7", 57600)` |
| Pengujian | `o.UseInMemory(network, address)` atau `o.UseInMemory(transport)` |

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Mavlink --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;

await using var link = MavlinkConnection.Create(o => o.UseUdp(14550));
await link.ConnectAsync();

await foreach (var p in link.ReadAllAsync())
{
    switch (p.Message)
    {
        case Attitude a: Console.WriteLine($"roll {a.Roll * 57.3:0.0}°  pitch {a.Pitch * 57.3:0.0}°"); break;
        case GlobalPositionInt g: Console.WriteLine($"{g.Lat / 1e7:0.000000}, {g.Lon / 1e7:0.000000}  {g.RelativeAlt / 1000.0:0.0} m"); break;
        case Statustext t: Console.WriteLine($"[{t.Severity}] {t.Text}"); break;
    }
}
```

## Ground station

```csharp
using var gcs = new MavlinkGroundStation(link, targetSystem: 1);
await gcs.WaitForHeartbeatAsync(TimeSpan.FromSeconds(5));

var result = await gcs.ArmAsync();               // COMMAND_LONG 400 → COMMAND_ACK (diulang dengan confirmation++)
if (result == MavResult.Accepted) await gcs.TakeoffAsync(15);

var parameters = await gcs.ReadParametersAsync(); // lengkap meski jaringan kehilangan paket (indeks yang hilang diminta ulang)
await gcs.SetParameterAsync("RTL_ALT", 2000);     // dikonfirmasi oleh PARAM_VALUE yang dikirim balik

Console.WriteLine($"{gcs.State.RelativeAltitude:0.0} m, {gcs.State.BatteryVoltage:0.00} V, armed: {gcs.State.Armed}");
```

Atur `gcs.ReadOnly = true` agar setiap perintah dan penulisan parameter melempar `ReadOnlyModeException`.

## Dialek Anda sendiri

Paket ini menyertakan generator. Tambahkan XML dialek Anda, beserta berkas yang di-include-nya, sebagai
`AdditionalFiles`:

```xml
<PropertyGroup>
  <MavlinkNamespace>MyCompany.Drone.Mavlink</MavlinkNamespace>
  <MavlinkDialectName>Payload</MavlinkDialectName>
</PropertyGroup>
<ItemGroup>
  <AdditionalFiles Include="mavlink/payload.xml" />
  <AdditionalFiles Include="$(MavlinkDialectsPath)minimal.xml" />
</ItemGroup>
```

Generator menghasilkan kelas pesan, enum, dan `PayloadDialect`. Gabungkan dengan dialek common:
`o.Dialect = new CompositeDialect(PayloadDialect.Instance, CommonDialect.Instance)`. XML yang tidak valid dan include
yang tidak ditemukan dilaporkan sebagai error kompilasi (MAV001, MAV002).

## Signing

```csharp
var signing = MavlinkSigning.FromPassphrase("my-shared-secret", linkId: 1);   // SHA-256 dari passphrase
await using var link = MavlinkConnection.Create(o => { o.UseUdp(14550); o.Signing = signing; });
```

Frame keluar ditandatangani dengan timestamp yang terus naik. Frame masuk dengan tanda tangan yang salah, dan frame
tanpa tanda tangan kecuali `AcceptUnsigned` diaktifkan, dibuang dan dihitung di `link.Parser.SignatureErrors`.

## Simulator dan alat

```bash
iotcom mavlink simulate --to 127.0.0.1:14550        # quadcopter lewat UDP (QGroundControl dan Mission Planner juga bisa terhubung)
iotcom mavlink listen --stats                       # laju pesan dan telemetri langsung
iotcom mavlink cmd arm --allow-write                # arm, disarm, takeoff 15, land, rtl
iotcom mavlink params                               # baca semua; ubah dengan: params RTL_ALT 2000 --allow-write
iotcom mavlink decode FE09000101000000000002035104037DDD
dotnet run --project samples/console/MavlinkTelemetry
```

## Pengujian dan interoperabilitas

- **Vektor bersama:** implementasi Python ketiga yang independen menghitung CRC_EXTRA untuk ke-235 pesan dari XML.
  Hasilnya sama dengan konstanta yang dipublikasikan (HEARTBEAT 50, ATTITUDE 39, COMMAND_LONG 152, …). C# hasil
  generator dan codec frame Rust (`iotcom-mavlink`, di-fuzz) sama-sama cocok dengannya pada frame v1 dan v2, termasuk
  pemotongan, payload yang seluruhnya nol, dan frame bertanda tangan.
- **Ujung ke ujung:** pengujian menerbangkan simulator lewat ground station: penolakan pre-arm, arm, lepas landas,
  mendarat, dan disarm otomatis. Pengujian juga mengunduh parameter lewat jaringan dengan kehilangan paket 30%,
  memeriksa mode read-only, serta berjalan lewat UDP sungguhan dan lewat stream.
- **Generator:** sebuah dialek kustom dikompilasi di proyek pengujian itu sendiri.

## Keterbatasan

Belum tersedia: helper protokol misi (pesan `MISSION_*` tersedia, tetapi state machine unggah/unduh belum), MAVLink
FTP, microservice kamera dan gimbal, serta routing antar beberapa link.

## Pelajari lebih lanjut

Demo Galeri *Telemetri drone lewat MAVLink* · notebook `notebooks/navigation/08-mavlink.id.ipynb` ·
[NMEA 0183](nmea.md) · `iotcom mavlink --help`
