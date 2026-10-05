---
title: HL7 v2 lewat MLLP
translation-status: synced
---

# HL7 v2 lewat MLLP

**Ringkasan.** HL7 versi 2 adalah format pesan yang masih dipakai sebagian besar perangkat dan sistem rumah sakit:
monitor pasien, analyzer laboratorium, sistem pendaftaran, dan interface engine. Pesan berupa teks ER7
(`MSH|^~\&|…`, satu segmen per baris) yang dikirim lewat TCP dengan framing **MLLP** (`0x0B` … `0x1C 0x0D`). Setiap
pesan dijawab dengan ACK. IoTCom.Net mengurai, membangun, mengirim, menerima, dan membalas pesan HL7 v2. Tersedia
juga simulator monitor pasien dengan pasien fiktif.

> **Bukan perangkat medis.** IoTCom.Net adalah pustaka komunikasi. Pustaka ini tidak tersertifikasi untuk
> diagnosis, pemantauan, atau keputusan terapi. Simulator dan demo hanya memakai pasien fiktif.

## Kapan dipakai

- Mengumpulkan tanda vital dari monitor pasien atau central station yang mengekspor HL7 v2 (ORU^R01).
- Menerima hasil dari analyzer laboratorium atau middleware, atau kejadian ADT dari sistem informasi rumah sakit.
- Membangun edge gateway yang meneruskan data perangkat ke MQTT, basis data, atau server FHIR.
- Menguji interface engine dengan aliran data perangkat yang realistis tanpa perangkat keras.

## Peran

| Peran | Tipe |
|---|---|
| Penerima / server / subscriber | `Hl7MllpServer` — banyak koneksi, ACK otomatis (AA, atau AR untuk masukan yang tak terurai), `MessageReceived`, `ReceiveAsync`, `SubscribeAsync("ORU^R01")` |
| Pengirim / client / publisher | `Hl7MllpClient` — `SendAsync` menunggu ACK yang MSA-2-nya cocok dengan control ID |
| Codec | `Hl7Message`, `Hl7MessageBuilder`, `Hl7Escaping`, `Hl7Time`, `MllpFraming` (sans-I/O) |
| Pembantu klinis | `Hl7Patient`, `Hl7Observation`, `VitalSigns` (kode LOINC), `GetPatient()`, `GetObservations()` |
| Simulator | `PatientMonitorSimulator` — perjalanan stabil, sepsis, hipoksia, dan hipertensi; `DemoWard` |

## Transport

`UseTcp(IPAddress.Any, 2575)` di penerima dan `UseTcp(host, 2575)` di pengirim. Port 2575 adalah port HL7 yang
terdaftar; banyak perangkat memakai port lain, jadi periksa spesifikasi antarmuka dari vendor. `UseInMemory(listener)`
tersedia untuk pengujian.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Hl7 --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Hl7;

await using var receiver = Hl7MllpServer.Create(o => o.UseTcp(IPAddress.Any, 2575));
receiver.MessageReceived += (_, e) =>
{
    var patient = e.Message.GetPatient();
    foreach (var obs in e.Message.GetObservations())
        Console.WriteLine($"{patient?.DisplayName}: {obs.Code.Text} = {obs.Value} {obs.Units} {obs.AbnormalFlag}");
};   // ACK AA dikirim otomatis setelah handler selesai
await receiver.StartAsync();
```

## Membaca pesan

Penomoran field mengikuti standar. MSH-1 adalah pemisah field, sehingga MSH-9 adalah tipe pesan. Indexer menerima
path bergaya terser:

```csharp
var m = Hl7Message.Parse(text);                 // pemisah segmen CR, LF, atau CRLF
m.MessageType                                   // "ORU^R01"
m["PID.5.1"]                                    // nama keluarga
m["OBX(2).5"]                                   // nilai OBX kedua
m.GetSegments("OBX").Count
Hl7Escaping.Unescape(@"Fever \T\ chills", Hl7Delimiters.Default)   // "Fever & chills"; \F\ \S\ \R\ \E\ .br dan \Xhh\ juga didukung
```

## Membangun pesan

```csharp
var oru = new Hl7MessageBuilder()
    .Header("MONITOR", "ICU", "IOTCOM", "HOSP", "ORU^R01^ORU_R01")
    .Patient(new Hl7Patient { Id = "MRN-001", FamilyName = "Santoso", GivenName = "Budi", Sex = "M" })
    .Segment("OBR", "1", "", "", "VITALS^Vital signs")
    .Observation(1, new Hl7Observation
    {
        Code = VitalSigns.HeartRate, Value = "118", Units = "/min",
        ReferenceRange = "60-100", AbnormalFlag = "H", Timestamp = DateTimeOffset.Now,
    })
    .Build();

var ack = await sender.SendAsync(oru);          // melempar DeviceException pada AE/AR; pakai throwOnNegativeAck: false untuk memeriksanya
Console.WriteLine(ack.AckCode());               // "AA"
```

`Segment(...)` meng-escape setiap nilai kecuali pemisah komponen `^`, sehingga `"VITALS^Vital signs"` tetap memiliki
dua komponen.

## ACK kustom

Isi `e.Ack` di handler untuk menjawab dengan selain AA. Pakai `WithoutAutoAck()` bila kode Anda mengirim ACK lewat
jalur lain.

```csharp
receiver.MessageReceived += (_, e) =>
{
    if (string.IsNullOrEmpty(e.Message.GetPatient()?.Id))
        e.Ack = e.Message.CreateAck("AE", "PID-3 patient identifier is required");
};
```

## Konfigurasi

| Opsi | Bawaan | Keterangan |
|---|---|---|
| `Encoding` | UTF-8 | encoding teks (perangkat lama sering memakai Latin-1 — set `Encoding.Latin1`) |
| `AutoAcknowledge` / `WithoutAutoAck()` | aktif | server menjawab setiap pesan |
| `AckTimeout` / `WithAckTimeout(...)` | 5 dtk | client: berapa lama menunggu ACK (`IoTComTimeoutException`) |
| `MllpFraming(maxMessageLength)` | 1 MiB | frame yang lebih besar ditolak (melindungi dari stream yang tak berujung) |

## Simulator

`PatientMonitorSimulator(patient, scenario, bed, seed, onset)` menghasilkan tanda vital yang masuk akal secara
fisiologis: HR, RR, SpO₂, NIBP, dan suhu, dengan noise dan perburukan yang dimulai pada `onset`. `ToOru(sample)`
menambahkan rentang rujukan dan flag H/L/N; `Admission(now)` menghasilkan ADT^A01; `FromOru(message)` membaca sampel
kembali. `PatientMonitorSimulator.DemoWard` menyediakan empat pasien fiktif, satu per skenario.

```bash
iotcom hl7 listen                                   # penerima MLLP di 2575 dengan observasi terurai
iotcom hl7 simulate --scenario sepsis --interval 2  # monitor pasien sintetis
iotcom hl7 send message.hl7                         # kirim berkas dan tampilkan ACK
dotnet run --project samples/console/Hl7MllpListener -- --simulate
```

## Pengujian dan interoperabilitas

Pengujian mencakup penguraian delimiter, escape sequence, repetisi dan sub-komponen, fragmen MLLP dan beberapa frame
dalam satu pembacaan, pencocokan ACK, penanganan AE/AR, serta perjalanan penuh simulator → MLLP → penerima. Parser
toleran terhadap akhir baris LF dan segmen kosong di akhir, yang sering muncul pada berkas ekspor interface engine
sungguhan.

## Keamanan

MLLP tidak memiliki autentikasi maupun enkripsi, sedangkan pesan HL7 berisi informasi kesehatan pribadi. Gunakan HL7
hanya di jaringan klinis yang terpisah atau lewat terowongan TLS/VPN. Catat metadata (tipe, control ID), bukan isi
pesan, dan perlakukan setiap field sebagai masukan yang tidak tepercaya.

## Keterbatasan

Hanya HL7 v2 (encoding ER7 v2.3–v2.8); encoding XML dan HL7 v3 di luar cakupan. Untuk FHIR, gunakan Firely SDK
bersama IoTCom.Net. Profil kesesuaian dan validasi Z-segment diserahkan ke aplikasi.

## Pelajari lebih lanjut

Notebook `notebooks/medical/05-hl7-dicom.id.ipynb` · demo Galeri *Monitor pasien ICU* ·
[Panduan AI medis](../guides/medical-ai.md) · [DICOM](dicom.md) · `iotcom hl7 --help`
