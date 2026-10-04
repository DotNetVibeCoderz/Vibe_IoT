# Changelog

All notable changes to IoTCom.Net. Versions follow SemVer; the native ABI version is tracked separately
(`iotcom_abi_version()`). *Bahasa Indonesia di bawah setiap rilis.*

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
