---
title: Perangkat medis + AI (contoh)
translation-status: synced
---

# Perangkat medis + AI (contoh)

Panduan ini menjelaskan dua demo medis di Galeri: bagaimana data perangkat sampai ke dasbor dan bagaimana LLM atau
model vision menambahkan ringkasan yang mudah dibaca. Setiap langkah adalah panggilan pustaka biasa yang bisa Anda
pakai ulang di gateway Anda sendiri.

> **Penting.** Ini adalah contoh rekayasa dengan pasien fiktif dan citra sintetis. Ini bukan perangkat medis.
> Keluaran AI bisa salah, dan keputusan selalu ada di tangan klinisi. Jangan mengirim data pasien sungguhan ke layanan
> AI eksternal tanpa persetujuan dan perjanjian yang disyaratkan di wilayah Anda.

![Monitor pasien ICU](../../images/gallery-hl7-icu.png)

## 1 · Monitor pasien → HL7 → dasbor klinis

```
PatientMonitorSimulator ──ORU^R01 lewat MLLP──▶ Hl7MllpServer ──▶ VitalsAnalyzer ──▶ papan bangsal + monitor
     (4 bed fiktif)                              (ACK otomatis)       NEWS2, tren,          └─▶ ClinicalAssistant (LLM)
                                                                        anomali                 catatan SBAR
```

1. **Transport.** Empat `PatientMonitorSimulator` (sepsis, stabil, hipoksia, hipertensi) mengirim pesan ORU^R01
   lewat `Hl7MllpClient`. Demo berjalan 10× lebih cepat dari waktu nyata.
2. **Penguraian.** `PatientMonitorSimulator.FromOru(message)` (atau `GetObservations()` dengan pemetaan LOINC Anda
   sendiri) mengubah segmen OBX menjadi `VitalsSample`.
3. **Analisis tanpa AI** (`VitalsAnalyzer`, di `samples/shared/IoTCom.Samples.Medical`):
   - **NEWS2** (Royal College of Physicians, 2017) per parameter dan totalnya, dengan pita risiko: rendah,
     rendah–sedang (satu nilai 3), sedang (5–6), atau tinggi (≥ 7). Contoh ini mengasumsikan udara ruangan dan
     pasien sadar penuh.
   - **Tren:** kemiringan kuadrat terkecil per jam dalam jendela data, ditambah proyeksi 15 menit dan NEWS2 yang
     dihasilkannya.
   - **Anomali:** z-score terhadap rata-rata berbobot eksponensial, untuk menangkap perubahan mendadak.
4. **Ringkasan AI.** `ClinicalAssistant.SummarizeAsync` mengirim deskripsi numerik yang ringkas (bukan pesan mentah)
   ke LLM dan meminta catatan SBAR paling banyak 180 kata. Bila AI tidak dikonfigurasi, templat berbasis aturan
   menulis catatannya. Setiap catatan diberi cap waktu dan NEWS2 yang menjadi dasarnya, karena papan terus berubah.

Penilaian bersifat deterministik dan dapat diuji, sehingga LLM hanya dipakai untuk *menjelaskan*, tidak untuk
*menilai*.

## 2 · Modalitas → DICOM → pra-baca model vision

![Pra-baca gambar dengan AI](../../images/gallery-dicom-ai.png)

```
SyntheticImaging ──C-STORE──▶ DicomStoreServer ──▶ DicomRenderer (window) ──PNG──▶ model vision ──▶ laporan JSON
 (CT / MR / X-ray,                (worklist PACS)                                                    temuan, kesan,
  temuan ditanam)                                                                                     keyakinan, urgen
```

1. Modalitas simulasi (`DicomStoreClient`, AE `SIM-MODALITY`) menyimpan studi sintetis ke PACS demo
   (`DicomStoreServer`).
2. Viewer merendernya dengan preset window. CT dapat ditampilkan dengan window paru, mediastinum, atau tulang.
3. `ClinicalAssistant.AnalyzeImageAsync` mengirim PNG hasil render beserta modalitas dan deskripsi studi, lalu
   meminta JSON: temuan, kesan, tingkat keyakinan, dan urgensi. `ParseReport` menerima JSON berpagar dan
   menyelamatkan jawaban yang terpotong.
4. Studi sintetis punya jawaban yang diketahui, sehingga `Mentions(report, truth)` menilai pra-baca. Fungsi ini
   mengabaikan negasi seperti "no pneumothorax" dan "tanpa efusi". Demo menampilkan chip *cocok dengan ground truth*.

![Pra-baca MRI](../../images/gallery-dicom-ai-mri.png)

## Mengonfigurasi penyedia AI

Contoh ini berbicara dengan API chat apa pun yang kompatibel dengan OpenAI. Anda bisa mengisi variabel lingkungan,
yang diutamakan, atau menulis `%APPDATA%/IoTCom.Net/ai.json` (`~/.config/IoTCom.Net/ai.json` di Linux dan macOS):

| Variabel | Kunci `ai.json` | Contoh |
|---|---|---|
| `IOTCOM_AI_PROVIDER` | `provider` | `azure`, `openai`, `huggingface`, `deepseek` |
| `IOTCOM_AI_ENDPOINT` | `endpoint` | `https://<resource>.openai.azure.com`, `https://router.huggingface.co/v1` |
| `IOTCOM_AI_KEY` | `apiKey` | kunci Anda |
| `IOTCOM_AI_MODEL` | `model` | model teks, mis. `gpt-5-mini` |
| `IOTCOM_AI_VISION_MODEL` | `visionModel` | model gambar (bawaan: sama dengan `model`) |
| `IOTCOM_AI_API_VERSION` | `apiVersion` | khusus Azure, bawaan `2025-04-01-preview` |

Azure memakai `/openai/deployments/{model}/chat/completions` dengan header `api-key`. Penyedia lain memakai
`{endpoint}/chat/completions` dengan token Bearer. Kunci tidak pernah dicatat atau ditampilkan; UI hanya menampilkan
nama penyedia dan model. Tanpa konfigurasi, kedua demo tetap berjalan dengan mesin berbasis aturan.

## Hasil pengukuran

Dengan Azure OpenAI (`gpt-5-mini` untuk teks, deployment GPT-5 yang mendukung vision untuk gambar):

- **Catatan SBAR** koheren, mengenali pola sepsis (HR/RR/suhu naik, tekanan darah turun), dan tiba dalam sekitar
  5 detik.
- **Pra-baca gambar:** 10 dari 12 pasangan modalitas/temuan sintetis benar. Yang meleset adalah pneumotoraks CT yang
  halus (dibaca normal) dan konsolidasi X-ray (dibaca sebagai massa). Phantom skematis bukan anatomi sungguhan, jadi
  anggap angka ini sebagai uji alur, bukan akurasi model.

## Melangkah lebih jauh

- Ganti simulator dengan perangkat sungguhan: `iotcom hl7 listen` dan `iotcom dicom listen` menampilkan apa yang
  dikirim perangkat Anda.
- Terbitkan snapshot ke MQTT (`AddMqtt`) atau teruskan ke server FHIR (Firely SDK) dari host yang sama.
- Simpan bagian deterministik (NEWS2, ambang, alarm) di kode, dan pakai model hanya untuk ringkasan, saran triase,
  atau pembacaan kedua.

Kode: `gallery/IoTCom.Net.Gallery/Demos/BedsideMonitorDemo.cs`, `ImagingDemo.cs`, dan
`samples/shared/IoTCom.Samples.Medical/`. Lihat juga [HL7 v2](../protocols/hl7.md) dan [DICOM](../protocols/dicom.md).
