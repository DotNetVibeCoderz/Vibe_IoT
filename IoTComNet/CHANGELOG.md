# Changelog

All notable changes to IoTCom.Net. Versions follow SemVer; the native ABI version is tracked separately
(`iotcom_abi_version()`). *Bahasa Indonesia di bawah setiap rilis.*

## 0.2.0-preview.1 — 2026-10-05

Healthcare: HL7 v2 and DICOM, plus medical demos with AI.

- **New package `IoTCom.Net.Protocols.Hl7`:**
  - ER7 parser and builder: delimiters, escaping, repetitions and sub-components, terser-style paths.
  - MLLP framing; `Hl7MllpServer` (auto-ACK AA/AR, custom ACKs, subscriptions) and `Hl7MllpClient` (waits for the
    ACK matching MSA-2).
  - LOINC vital-sign helpers and `PatientMonitorSimulator` (stable, sepsis, hypoxia, hypertension).
  - Also in the `IoTCom.Net` meta-package, with `AddHl7Server` / `AddHl7Client`.
- **New package `IoTCom.Net.Adapters.Dicom`:**
  - Storage SCP/SCU (C-STORE, C-ECHO) over fo-dicom in the endpoint model.
  - Windowed grayscale renderer to PNG.
  - Synthetic CT/MR/X-ray studies with planted ground-truth findings.
- **Gallery:** new *Medical & healthcare* category.
  - *ICU bedside monitors:* HL7/MLLP, NEWS2, trends, anomalies and an LLM SBAR note.
  - *Imaging AI pre-read:* DICOM C-STORE, windowed viewer, and a vision-model pre-read scored against ground truth.
  - Any OpenAI-compatible provider works (Azure OpenAI, OpenAI, Hugging Face, DeepSeek); without one, a rule-based
    fallback is used.
- **CLI:** `iotcom hl7 listen|send|simulate`, `iotcom dicom listen|send|echo`.
- **Samples:** `Hl7MllpListener` console sample; notebook pair `medical/05-hl7-dicom`.
- **Docs:** HL7 and DICOM pages and the *Medical devices + AI* guide (EN/ID).

Not a medical device. All patients are fictional and all images are synthetic.

*Kesehatan: paket baru HL7 v2 (MLLP, ACK, simulator monitor pasien) dan adapter DICOM (C-STORE/C-ECHO, renderer,
studi sintetis); demo Galeri monitor ICU (NEWS2 + catatan SBAR dari LLM) dan pra-baca citra dengan model vision; perintah CLI
`hl7` / `dicom`; sampel, notebook, dan dokumentasi dua bahasa. Bukan perangkat medis.*

## 0.1.0-preview.1 — 2026-10-05

First preview (Phase 0 of the [roadmap](PLAN.md)).

- **Core:** endpoint model (client/server/publisher/subscriber), `ITransport` over System.IO.Pipelines, TCP and
  in-memory transports, traffic tap and frame anatomy, OpenTelemetry metrics/traces, reconnect policy, native loader.
- **Framing:** CRC catalogue (23 presets), LRC, SLIP, COBS, HDLC, line framing, streaming decoders.
- **Modbus:** TCP/RTU/ASCII master and slave, virtual PLC simulator, read-only mode, device identification;
  Rust engine (`IoTCom.Net.Native.Modbus`, native ABI 1).
- **NMEA 0183**, **Art-Net 4 / sACN**, **MQTT adapter + broker**, **SenML**, **serial transport**, **hosting**.
- **Tools:** `iotcom` CLI, Gallery desktop app, gateway web sample, console samples, templates, notebooks.
- **Docs:** 25 pages in English and Bahasa Indonesia.

*Rilis pratinjau pertama (Fase 0): inti, framing, Modbus (C# + mesin Rust), NMEA, Art-Net/sACN, MQTT, SenML, serial,
hosting, CLI, Galeri, sampel, template, notebook, dan dokumentasi dua bahasa.*
