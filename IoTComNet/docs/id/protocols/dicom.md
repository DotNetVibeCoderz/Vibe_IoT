---
title: DICOM (adapter di atas fo-dicom)
translation-status: synced
---

# DICOM (adapter di atas fo-dicom)

**Ringkasan.** DICOM adalah standar citra medis: CT, MR, X-ray, USG. Modalitas mengirim gambar ke PACS dengan
**C-STORE** dan memeriksa konektivitas dengan **C-ECHO**. DICOM besar dan sudah matang, jadi IoTCom.Net tidak
membangunnya ulang. `IoTCom.Net.Adapters.Dicom` membungkus [fo-dicom](https://github.com/fo-dicom/fo-dicom) (MS-PL)
dalam model endpoint IoTCom: lifecycle, traffic tap, metrik, dan subscription. Adapter ini juga menambahkan renderer
dengan windowing dan pembangkit studi sintetis.

> **Bukan perangkat medis.** Renderer ditujukan untuk pratinjau dan alur pemrosesan, bukan tampilan diagnostik.
> Studi sintetis adalah phantom skematis dengan temuan yang ditanam; bukan anatomi sungguhan.

## Kapan dipakai

- Menerima gambar dari modalitas atau router di edge, misalnya untuk meneruskan, menganonimkan, atau menjalankan
  pra-baca AI.
- Mengirim studi ke PACS atau layanan AI dari gateway.
- Menguji alur DICOM dan prompt AI dengan gambar deterministik yang jawaban benarnya sudah diketahui.

## Peran

| Peran | Tipe |
|---|---|
| Storage SCP (server / subscriber) | `DicomStoreServer` — C-STORE dan C-ECHO, `ImageReceived`, `ReceiveAsync`, `SubscribeAsync("CT")` (filter per modalitas) |
| Storage SCU (client) | `DicomStoreClient` — `EchoAsync`, `StoreAsync` (melempar `DeviceException` bila status gagal) |
| Rendering | `DicomRenderer.Render(dataset, window)` → `GrayImage` → `ToPng()`; `DicomRenderer.Presets` |
| Studi sintetis | `SyntheticImaging.Generate(modality, finding, …)` — CT dada, MR otak, X-ray dada |

## Instalasi

```bash
dotnet add package IoTCom.Net.Adapters.Dicom --prerelease
```

Paket ini terpisah dari meta-package `IoTCom.Net` karena fo-dicom tidak trimmable dan tidak kompatibel NativeAOT.

## Mulai cepat

```csharp
using IoTCom.Net.Adapters.Dicom;

await using var pacs = DicomStoreServer.Create(o => { o.Port = 11112; o.AeTitle = "EDGE-PACS"; });
pacs.ImageReceived += r =>
{
    Console.WriteLine($"{r.Modality} {r.StudyDescription} for {r.PatientName} from {r.CallingAe}");
    File.WriteAllBytes($"{r.SopInstanceUid}.png", DicomRenderer.Render(r.File.Dataset).ToPng());
};
await pacs.StartAsync();

await using var modality = DicomStoreClient.Create(o => { o.Port = 11112; o.CalledAe = "EDGE-PACS"; });
await modality.EchoAsync();
await modality.StoreAsync(await FellowOakDicom.DicomFile.OpenAsync("image.dcm"));
```

## Rendering dan windowing

`Render` menangani data piksel 8 dan 16 bit, bertanda maupun tidak, dan menerapkan rescale slope dan intercept
(Hounsfield unit untuk CT). Gambar MONOCHROME1 dibalik, lalu window VOI diterapkan: window yang Anda berikan, atau
window dari dataset, atau min/maks. Transfer syntax terkompresi dan gambar berwarna melempar
`NotSupportedException`; dekode dulu dengan codec fo-dicom.

| Preset | Center / width |
|---|---|
| `CT lung` | −600 / 1500 |
| `CT mediastinum` | 40 / 400 |
| `CT bone` | 400 / 1800 |
| `CT brain` | 40 / 80 |

## Studi sintetis

`SyntheticImaging.Generate(modality, finding, patientName, patientId, seed)` menghasilkan berkas DICOM yang valid
dengan anatomi skematis dan temuan yang bisa ditanam. Gunakan `FindingsFor(modality)` untuk melihat temuan yang
didukung tiap modalitas:

| Modalitas | Temuan |
|---|---|
| CT dada (512², HU) | nodul paru, pneumotoraks, konsolidasi, efusi pleura |
| MR otak (256²) | massa, infark |
| X-ray dada (768²) | nodul paru, pneumotoraks, konsolidasi, kardiomegali, efusi pleura |

Setiap modalitas juga mendukung `None` (studi normal). `ImageComments` menandai objek sebagai SYNTHETIC dan mencatat
ground truth, sehingga pra-baca AI bisa dinilai otomatis (lihat [panduan AI medis](../guides/medical-ai.md)).

```bash
iotcom dicom listen --output received/            # Storage SCP yang menyimpan .dcm + pratinjau .png
iotcom dicom send --synthetic ct --finding Pneumothorax
iotcom dicom echo --port 11112 --aec ANY-SCP
```

## Pengujian dan interoperabilitas

Pengujian menjalankan C-ECHO dan C-STORE lewat TCP sungguhan, merender studi sintetis, dan memeriksa bahwa setiap
pasangan modalitas/temuan menghasilkan objek yang valid. fo-dicom sendiri diuji terhadap suite kesesuaian DICOM dan
PACS dari banyak vendor.

## Keamanan

Asosiasi DICOM tidak diautentikasi selain lewat AE title, sedangkan header DICOM berisi informasi kesehatan pribadi.
Batasi SCP hanya untuk AE title yang dikenal (`AcceptAnyCalledAe = false`) dan untuk jaringan klinis atau VPN. Jangan
pernah mengirim studi yang dapat diidentifikasi ke layanan AI eksternal tanpa perjanjian pemrosesan data dan
de-identifikasi.

## Keterbatasan

Hanya penyimpanan (C-STORE, C-ECHO). Query/retrieve (C-FIND, C-MOVE), worklist, dan DICOMweb tersedia langsung di
fo-dicom. Renderer hanya menampilkan satu frame dan hanya grayscale.

## Pelajari lebih lanjut

Notebook `notebooks/medical/05-hl7-dicom.id.ipynb` · demo Galeri *Pra-baca gambar dengan AI* · [HL7 v2](hl7.md) ·
[Panduan AI medis](../guides/medical-ai.md) · `iotcom dicom --help`
