---
title: Arsitektur
translation-status: synced
---

# Arsitektur

## Tiga keputusan

1. **Kurasi ketat.** Yang sudah dikerjakan .NET dengan baik tidak dibuat ulang. TCP, UDP, TLS, QUIC, HTTP, WebSocket,
   gRPC, port serial, GPIO, JSON, CBOR, dan kriptografi berasal dari BCL atau paket resmi Microsoft. Library komunitas
   yang matang dibungkus sebagai adapter. Hanya celah nyata yang diimplementasikan.
2. **Rust untuk low-level.** Akses hardware dan state machine biner kompleks yang layak difuzz serta butuh keamanan
   memori ditulis dengan Rust dan diekspos lewat C ABI. API publik, DI, async, dan integrasi ASP.NET Core tetap C#.
3. **Pengalaman lengkap.** Library, CLI, sampel, Galeri, notebook, template, dan dokumentasi dua bahasa dirilis bersama.

## Tier kurasi

| Tier | Arti | Tindakan | Contoh |
|---|---|---|---|
| **T0** BCL / resmi | Sudah ada di .NET | Tidak dibuat ulang; dipakai sebagai transport | `System.Net.Sockets`, `SslStream`, `System.IO.Ports`, `System.Formats.Cbor` |
| **T1** Adapter | Ada library .NET matang | Dibungkus di balik kontrak IoTCom | MQTT → MQTTnet |
| **T2** Implementasi | Celah nyata | Dibuat dengan Rust (T2-R) atau C# (T2-C) | Modbus (C# + mesin Rust), NMEA, Art-Net, sACN, SenML, framing |
| **T3** Di luar cakupan | Radio/PHY/firmware | Hanya didokumentasikan | LoRa PHY, radio Zigbee, 5G |

## Lapisan

```
┌───────────────────────────────────────────────────────────────────────────┐
│ Aplikasi: Galeri (Avalonia) · Gateway (ASP.NET Core) · sampel console · notebook
├───────────────────────────────────────────────────────────────────────────┤
│ Perkakas: CLI iotcom · template dotnet new · perkakas screenshot & docs    │
├───────────────────────────────────────────────────────────────────────────┤
│ IoTCom.Net.Hosting — AddIoTCom(), siklus hidup ter-host, health check      │
├───────────────────────────────────────────────────────────────────────────┤
│ Protokol (C#): Modbus · NMEA · Art-Net/sACN · SenML · Framing              │
│ Adapter (T1):  MQTT (MQTTnet)                                              │
├───────────────────────────────────────────────────────────────────────────┤
│ IoTCom.Net.Core — ITransport di atas System.IO.Pipelines, EndpointBase,    │
│                   traffic tap, diagnostik, reconnect, loader native        │
├─────────────────────────────────┬─────────────────────────────────────────┤
│ Transport (BCL): TCP · serial   │ Jembatan native: LibraryImport · SafeHandle│
│ · UDP · in-memory               │ · loader RID · cek versi ABI             │
├─────────────────────────────────┴─────────────────────────────────────────┤
│ Rust: iotcom-core (Machine) · iotcom-ffi-support · iotcom-modbus · cdylib  │
└───────────────────────────────────────────────────────────────────────────┘
```

## Sans-I/O

Logika protokol tidak pernah menyentuh socket atau port. *Machine* protokol menerima byte, perintah, dan tick timer,
lalu menghasilkan byte untuk dikirim, event, dan deadline berikutnya:

```rust
pub trait Machine {
    type Event;
    type Command;
    fn handle_input(&mut self, now: Instant, bytes: &[u8]) -> Result<()>;
    fn handle_command(&mut self, now: Instant, cmd: Self::Command) -> Result<()>;
    fn handle_timeout(&mut self, now: Instant);
    fn poll_transmit(&mut self, out: &mut Vec<u8>) -> Option<Transmit>;
    fn poll_event(&mut self) -> Option<Self::Event>;
    fn poll_timeout(&self) -> Option<Instant>;
}
```

Konsekuensinya: transport apa pun bekerja tanpa perubahan (TCP, serial, TLS, file replay, pipe in-memory), pengujian
deterministik (waktu adalah parameter), tidak butuh runtime async di sisi Rust, dan permukaan FFI tetap sempit.
Protokol C# managed mengikuti pemisahan yang sama: codec (`ModbusPdu`, `ModbusFraming`, `NmeaParser`, `ArtNetPacket`)
adalah fungsi murni; endpoint hanya menjalankan I/O.

## Rust atau C#?

**Rust** bila salah satu terpenuhi: akses OS/hardware yang tidak ada di BCL (CAN, USB, BLE, raw Ethernet, SWD); codec
atau state machine biner kompleks yang layak difuzz (mesin Modbus, DLMS, IEC 104, MAVLink, ISO-TP/UDS); ada crate
matang dengan lisensi kompatibel; atau jalur panas.

**C#** untuk protokol teks/JSON/HTTP, protokol UDP/TCP sederhana (Art-Net, sACN, NMEA), helper kecil tanpa state (CRC,
COBS, SLIP — panggilan FFI lebih mahal dari fungsinya), adapter, dan semua API publik.

Helper kecil yang ada di kedua sisi dijaga tetap sinkron dengan **test vector bersama** di `/conformance`, yang
dijalankan oleh test suite C# maupun Rust. Uji lintas bahasa juga memastikan mesin Modbus Rust dan framing managed
menghasilkan frame yang identik byte per byte.

## Paket dan meta-paket

Satu paket per protokol, sehingga aplikasi hanya membawa yang dipakai; `IoTCom.Net` membundel set umum. Mesin Rust
dirilis terpisah (`IoTCom.Net.Native.Modbus`) dengan satu library native per RID di `runtimes/{rid}/native/`
(satu `cdylib` per paket protokol, `iotcom-core` ter-link statis — ADR-003).

Lihat juga: [Endpoint & transport](endpoints-and-transports.md) · [Lapisan native](../native/rust-ffi.md)
