---
title: DLMS/COSEM
translation-status: synced
---

# DLMS/COSEM

**Ringkasan.** DLMS/COSEM (IEC 62056) adalah cara meter listrik, serta banyak meter gas dan air, dibaca dan dikelola.
Meter menyediakan **objek COSEM** yang masing-masing dinamai **kode OBIS** (`1-0:1.8.0*255` adalah energi aktif yang
diimpor). Client membuka **asosiasi**, lalu melakukan GET pada atribut, SET, atau memanggil method. IoTCom.Net
mengimplementasikan:

- kedua link: **HDLC** (IEC 62056-46, port optik dan RS-485) dengan segmentasi, serta **wrapper TCP**
  (IEC 62056-47, port 4059);
- data **A-XDR**, kode OBIS beserta katalognya, register dengan scaler dan unit, date-time COSEM;
- asosiasi dengan keamanan **tanpa autentikasi**, **rendah** (password), dan **tinggi** (GMAC, mekanisme 5), serta
  **APDU terenkripsi** (security suite 0, AES-GCM) dengan perlindungan replay;
- **GET** dengan block transfer dan selective access, **SET**, dan **ACTION**;
- **server** meter dan **simulator**: meter rumah tangga tiga fase dengan panel surya atap, tarif waktu PLN, load
  profile 15 menit, dan relay suplai.

![Pembacaan smart meter](../../images/gallery-metering.png)

## Kapan digunakan

- Membaca meter di meja atau di lapangan lewat probe optik, bus RS-485, atau gateway TCP.
- Membangun perangkat lunak head-end atau pengumpul data yang mengambil register dan load profile.
- Menguji perangkat lunak Anda terhadap meter yang Anda kendalikan, termasuk password salah, penulisan yang ditolak,
  dan profil yang panjang.

## Peran

| Peran | Tipe |
|---|---|
| Client (pembaca meter) | `DlmsClient`: `GetAsync`, `SetAsync`, `ActionAsync`, `ReadRegisterAsync`, `ReadClockAsync`, `ReadObjectListAsync`, `ReadProfileAsync` |
| Server (meter) | `DlmsServer` dengan `CosemObject`: `CosemRegister`, `CosemClock`, `CosemProfileGeneric`, `CosemDisconnectControl`, `CosemDataObject`, `CosemAssociationLn` |
| Simulator | `DlmsMeterSimulator`: energi (impor/ekspor, per tarif), nilai per fase, jam, load profile, relay |
| Codec | `ObisCode`, `CosemData`, `HdlcFrame`, `DlmsWrapper`, `DlmsApdu`, `DlmsSecurity`, `DlmsAnatomy` (sans-I/O) |

## Asosiasi dan hak akses

| Client | SAP | Autentikasi | Boleh |
|---|---|---|---|
| Public client | 16 | tidak ada | membaca |
| Management client | 1 | password (LLS), `WithPassword("…")` | membaca, menulis, memanggil method |
| Management client | 1 | tantangan GMAC (HLS 5) dengan APDU terenkripsi, `WithHighSecurity(keys)` | membaca, menulis, memanggil method |

Client **read-only secara default**: `SetAsync` dan `ActionAsync` melempar `ReadOnlyModeException` sampai Anda
mengatur `ReadOnly = false`. Meter juga menegakkan aturannya sendiri dan menjawab `ReadWriteDenied` kepada public
client.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Dlms --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Transport.Serial;

await using var meter = DlmsClient.Create(o => o.UseSerial("COM3", 9600));   // probe optik, HDLC, public client
await meter.ConnectAsync();
Console.WriteLine(await meter.ReadClockAsync());
Console.WriteLine(await meter.ReadRegisterAsync(ObisCode.Parse("1-0:1.8.0*255")));   // 4842792 Wh

var now = await meter.ReadClockAsync();
var profile = await meter.ReadProfileAsync(ObisCode.Parse("1.0.99.1.0.255"), now.AddDays(-1), now);
foreach (var row in profile.Rows) Console.WriteLine(string.Join("  ", row));
```

Lewat TCP, gunakan `o.UseTcp(host, 4059)` dengan `o.Framing = DlmsFraming.Wrapper` (atau tetap HDLC untuk gateway
yang menyalurkannya). Atur `ServerPhysicalAddress` ke alamat bawah HDLC meter (sering 16 + digit terakhir nomor
seri).

## Menyajikan meter

```csharp
await using var server = DlmsServer.Create(o => { o.UseTcp(IPAddress.Any, 4059); o.Framing = DlmsFraming.Wrapper; });
server.Add(new CosemRegister(ObisCode.Parse("1.0.1.8.0.255"), () => energyWh, scaler: 0, CosemUnit.WattHour));
server.Add(new CosemClock(ObisCode.Parse("0.0.1.0.0.255"), () => DateTimeOffset.Now));
await server.StartAsync();
```

Atau biarkan `new DlmsMeterSimulator(server)` mengisi meter lengkap. Objek asosiasi (0.0.40.0.0.255) dan daftar
objeknya ditambahkan otomatis.

## Bagaimana lapisan protokol tersusun

| Lapisan | Yang terjadi |
|---|---|
| HDLC | SNRM/UA menegosiasikan ukuran information field dan window; I-frame membawa `E6 E6 00` + APDU; APDU panjang disegmentasi dan setiap segmen di-ACK dengan RR; DISC mengakhiri link |
| Wrapper | header 8 byte (versi 1, wPort sumber dan tujuan, panjang) di depan setiap APDU |
| ACSE | AARQ/AARE: application context (LN, dengan atau tanpa ciphering), mekanisme, password atau tantangan, conformance block, ukuran PDU maksimum |
| xDLMS | GET/SET/ACTION; hasil GET yang panjang dikirim dalam blok (`GET.response with-datablock`) yang diminta client satu per satu |
| Keamanan | APDU terenkripsi: tag, security control `0x30`, invocation counter, ciphertext AES-GCM, dan tag 12 byte; IV adalah system title pengirim dan counter |

## Alat

```bash
iotcom dlms read --sim                                 # meter simulasi
iotcom dlms read --serial COM3 1.0.1.8.0.255 1.0.32.7.0.255
iotcom dlms objects -h 10.0.0.30                        # wrapper di TCP 4059
iotcom dlms profile --sim --hours 6
iotcom dlms relay off --sim --password 12345678 --allow-write
iotcom dlms simulate --port 4059 --frames
dotnet run --project samples/console/DlmsMeterReader
```

## Pengujian dan interoperabilitas

- **Codec:** C# dan crate Rust `iotcom-dlms` menjalankan vektor yang sama (`/conformance/dlms.json`): frame HDLC
  (termasuk SNRM klasik `7E A0 07 03 21 93 0F 01 7E`, HCS dan FCS yang salah) dan nilai A-XDR dalam bentuk teks
  kanonik. Vektor dihasilkan oleh implementasi Python terpisah. Codec Rust di-fuzz (`cargo fuzz run dlms`).
- **APDU:** AARQ public client cocok byte demi byte dengan referensi yang diterbitkan; PDU GET, blok, dan terenkripsi
  round trip.
- **Sesi:** HDLC dan wrapper, pembacaan public client (register, jam, daftar objek lewat block transfer dan segmen
  64 byte, profil berdasarkan rentang tanggal), penulisan LLS dan password yang ditolak, HLS dengan setiap APDU
  terenkripsi, key yang salah ditolak.

## Keamanan

Password dan key adalah rahasia: jangan pernah mencatat (log) atau meng-commit-nya (contoh memakai key uji yang
diterbitkan). LLS mengirim password tanpa enkripsi; utamakan HLS dengan ciphering di jaringan yang tidak Anda
kendalikan. Biarkan pembaca tetap read-only, dan perlakukan pemutusan relay sebagai operasi atas suplai pelanggan.
CLI mewajibkan `--allow-write` dan konfirmasi.

## Keterbatasan

Hanya logical-name referencing (tanpa short name). Belum tersedia: security suite 1 dan 2 (ECDSA, ECDH), dedicated
key, general-block-transfer dan general-protection, data notification dan push, handshake mode E IEC 62056-21
(jalankan meter dalam HDLC), image transfer, dan pustaka kelas Blue Book yang lengkap.

## Pelajari lebih lanjut

Demo Galeri *Pembacaan smart meter* · notebook `notebooks/metering/10-dlms-mbus.id.ipynb` · [M-Bus](mbus.md) ·
`iotcom dlms --help`
