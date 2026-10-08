<p align="center"><img src="assets/icon/icon-256.png" width="112" alt="IoTCom.Net"></p>

<h1 align="center">IoTCom.Net</h1>

<p align="center"><b>Library protokol komunikasi IoT yang lengkap untuk .NET 10 — dengan inti Rust di bagian yang penting.</b><br>
Dibuat oleh <b>Gravicode Studios</b> dipimpin oleh <b>Kang Fadhil</b> · <a href="README.md">English</a></p>

<p align="center">
<img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
<img alt="Rust" src="https://img.shields.io/badge/Rust-stable-B7410E">
<img alt="Lisensi" src="https://img.shields.io/badge/license-Apache--2.0-2E9E5B">
<img alt="Status" src="https://img.shields.io/badge/status-preview-F2A900">
</p>

![Galeri IoTCom.Net — PLC pabrik pintar](docs/images/gallery-dark-id.png)

Satu model yang konsisten — **client, server, publisher, subscriber** — untuk protokol industri, otomotif, navigasi,
pencahayaan, pesan, dan kesehatan. Setiap protokol dilengkapi **simulator**, sehingga semuanya (pengujian, sampel, notebook,
Galeri) berjalan tanpa perangkat keras. Yang sudah dikerjakan .NET dengan baik tidak dibuat ulang; library yang matang
dibungkus; hanya celah nyata yang diimplementasikan — dengan C#, atau dengan Rust yang aman memori di balik C ABI yang
sempit.

```csharp
await using var plc = ModbusClient.Create(o => o.UseTcp("192.168.1.10", 502).WithUnitId(1));
ushort[] registers = await plc.ReadHoldingRegistersAsync(address: 0, count: 10);
```

## Isi

| | |
|---|---|
| **Modbus TCP / RTU / ASCII** | master + slave + simulator PLC virtual, pipelining TCP, mode aman read-only, identifikasi perangkat — dan **mesin Rust** opsional (`NativeModbusClient`) |
| **OPC UA** | adapter di atas stack OPC Foundation: jelajah, baca, tulis di balik saklar read-only, pemanggilan method, subscription sebagai `IAsyncEnumerable`, endpoint aman; server simulator pabrik |
| **MAVLink v1 / v2** | dialek common lengkap dari XML resmi (source generator Roslyn — bawa dialek Anda sendiri), signing, link UDP/TCP/serial, helper ground station, simulator quadcopter; codec frame dicerminkan di Rust |
| **NMEA 0183 · AIS** | parser dan builder dengan validasi checksum, penguraian AIS (tipe 1–5, 18, 19, 21, 24, 27) dengan pelacak kapal, GGA/RMC/GSA/GSV/VTG/GLL/ZDA bertipe, agregator fix GNSS, server NMEA, simulator GPS |
| **Art-Net 4 · sACN (E1.31)** | DMX512 lewat IP: kirim, terima, discovery ArtPoll, multicast, prioritas, model universe dengan fade |
| **CoAP (RFC 7252)** | client + server lewat UDP: pengiriman ulang dan deduplikasi, Observe, Block-wise, penemuan link-format, SenML, simulator rumah kaca; codec dicerminkan oleh crate Rust yang di-fuzz |
| **LoRaWAN 1.0.x** | network server ringan untuk gateway Semtech UDP (OTAA/ABP, deduplikasi antar-gateway, downlink Class A, MAC command), packet forwarder, MAC end device, EU868/US915/AS923-2, airtime, simulator gateway + sensor; codec dicerminkan oleh crate Rust yang di-fuzz |
| **DLMS/COSEM · M-Bus** | pembacaan smart meter lewat HDLC atau TCP: register OBIS, load profile dengan selective access, LLS/HLS dengan enkripsi AES-GCM, kendali relay di balik default read-only, simulator meter; master M-Bus berkabel dengan pemindaian, alamat sekunder, dan penguraian record, simulator panas/air/listrik; codec dicerminkan oleh crate Rust yang di-fuzz |
| **Perintah AT · ASTM** | modem seluler/GNSS (penguraian URC, SMS, mode read-only, simulator modul); analyzer lab lewat ASTM E1394/E1381 dengan pengiriman ulang NAK dan jembatan HL7 ORU |
| **MQTT 3.1.1 / 5.0 · Sparkplug B** | adapter di atas MQTTnet: subscription `IAsyncEnumerable`, reconnect + resubscribe, helper JSON/SenML, broker tertanam; edge node dan host application Sparkplug B (birth, alias, will NDEATH dengan bdSeq, rebirth, penulisan terjaga) |
| **mDNS / DNS-SD** | responder dan browser (RFC 6762/6763): pengumuman, known-answer suppression, goodbye, cache TTL, enumerasi tipe, simulator pabrik |
| **CAN / CAN FD** | satu `ICanBus` untuk Linux SocketCAN, adapter USB slcan (CANable, CANtact), dan bus virtual; notasi candump, reader terfilter |
| **Bluetooth LE** | central lewat pustaka Rust di atas btleplug (WinRT, BlueZ, CoreBluetooth): pindai, GATT read/write/notify dengan saklar read-only; codec advertising data, iBeacon, Eddystone, dan nilai GATT; radio virtual |
| **USB · HID** | transfer control/bulk/interrupt mentah (Rust nusb) dan report HID (hidapi) dengan saklar read-only, transport aliran byte `UseUsbBulk`, papan relay USB HID, bus virtual |
| **ISO-TP · UDS · OBD-II** | ISO 15765-2 sebagai state machine **Rust** yang di-fuzz; tester UDS (session, security access, DID, DTC, routine, mode read-only), scan tool OBD-II, dan simulator ECU |
| **HL7 v2 · MLLP** | parser/builder ER7 dengan escaping, pengirim/penerima MLLP dengan pencocokan ACK, tanda vital LOINC, simulator monitor pasien (sepsis, hipoksia, …) |
| **DICOM** | adapter di atas fo-dicom: Storage SCP/SCU (C-STORE, C-ECHO), renderer dengan window ke PNG, studi CT/MR/X-ray sintetis dengan temuan yang ditanam |
| **Codec payload** | SenML (RFC 8428, JSON + CBOR), adapter Protobuf dan MessagePack dengan inspeksi tanpa skema, TLV sederhana dan BER-TLV (EMV); satu kontrak `IPayloadCodec<T>` |
| **Framing** | katalog CRC (23 preset, 8–64 bit), LRC, SLIP, COBS, HDLC, decoder streaming di atas `System.IO.Pipelines` |
| **Core** | transport TCP / serial / in-memory, traffic tap (*frame lane*), metrik & trace OpenTelemetry, kebijakan reconnect, hosting + health check |

Ditambah: aplikasi desktop **Galeri IoTCom.Net**, **CLI `iotcom`**, **ekstensi VS Code** (penampil frame, pemantau lalu lintas), sampel web **edge gateway** dengan dashboard HMI
langsung, sampel console, **template** `dotnet new`, **notebook** Polyglot, dan **dokumentasi dalam English dan
Bahasa Indonesia**.

## Lihat

<table>
<tr><td><img src="docs/images/gateway-dashboard.png" alt="Dashboard gateway"><br><sub>Edge gateway: Modbus → MQTT dengan dashboard HMI langsung dan isi jalur yang terurai</sub></td>
<td><img src="docs/images/gallery-traffic.png" alt="Lalu lintas Galeri"><br><sub>Galeri: setiap frame diurai per field</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-can-uds.png" alt="Diagnostik kendaraan"><br><sub>Diagnostik kendaraan: OBD-II dan UDS lewat CAN, ISO-TP di Rust, terhadap ECU mesin simulasi</sub></td></tr>
<tr><td><img src="docs/images/gallery-hl7-icu.png" alt="ICU"><br><sub>Monitor pasien ICU lewat HL7/MLLP: NEWS2, tren, dan catatan SBAR dari LLM</sub></td>
<td><img src="docs/images/gallery-dicom-ai.png" alt="DICOM AI"><br><sub>DICOM C-STORE → viewer dengan window → pra-baca model vision</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-mavlink.png" alt="Drone MAVLink"><br><sub>Telemetri drone lewat MAVLink: artificial horizon, jejak terbang, dan perintah ber-ACK</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-coap.png" alt="Rumah kaca CoAP"><br><sub>Rumah kaca CoAP: Observe, Block-wise, dan diagram urutan pesan langsung di jaringan yang kehilangan paket</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-metering.png" alt="Pembacaan smart meter"><br><sub>Pembacaan smart meter: DLMS/COSEM pada faceplate LCD, dua hari load profile, dan sub-meter M-Bus</sub></td></tr>
<tr><td colspan="2"><img src="docs/images/gallery-lorawan.png" alt="Monitor jaringan LoRaWAN"><br><sub>Monitor jaringan LoRaWAN: gateway, jangkauan per spreading factor, dan setiap uplink di peta radio, lewat network server ringan</sub></td></tr>
<tr><td><img src="docs/images/gallery-nmea.png" alt="NMEA"><br><sub>Pelacak GNSS lewat NMEA 0183</sub></td>
<td><img src="docs/images/gallery-lighting.png" alt="Art-Net"><br><sub>Lampu panggung lewat Art-Net</sub></td></tr>
<tr><td><img src="docs/images/gallery-workbench.png" alt="Meja kerja"><br><sub>Meja kerja frame & checksum</sub></td>
<td><img src="docs/images/cli.png" alt="CLI"><br><sub><code>iotcom</code> — urai, baca, CRC dari terminal</sub></td></tr>
</table>

## Mulai

```bash
dotnet add package IoTCom.Net --prerelease            # library (meta-paket)
dotnet tool install -g IoTCom.Net.Cli --prerelease    # CLI iotcom
dotnet new install IoTCom.Net.Templates               # dotnet new iotcom-console / iotcom-worker

iotcom modbus serve --port 1502 --simulate            # PLC virtual…
iotcom modbus read --port 1502 --table input --count 8 --watch 1000   # …dan tampilan langsung
```

Baca [mulai cepat](docs/id/getting-started/quickstart.md), lalu jelajahi [dokumentasi](docs/id/index.md).

## Menjalankan aplikasi dari source

```bash
dotnet run --project gallery/IoTCom.Net.Gallery                          # Galeri desktop
dotnet run --project samples/web/IoTCom.Gateway --urls http://localhost:5080
dotnet run --project samples/console/ModbusMaster -- --simulate
```

## Build dan pengujian

```bash
dotnet build IoTCom.Net.slnx
dotnet test tests/IoTCom.Net.Tests                  # 300+ uji termasuk conformance lintas bahasa
cd rust && cargo test --workspace && cargo build --release   # inti Rust + library native iotcom_modbus
python build/check_docs_parity.py                   # paritas EN/ID + cek tautan
```

## Struktur repositori

```
src/            paket C# (Abstractions, Core, Framing, Transport.Serial, Transport.Can, Protocols.*, Protocols.Hl7, Adapters.Mqtt, Adapters.Dicom, Serialization.SenML, Hosting, Native.Modbus, meta)
rust/           workspace Rust: iotcom-core (Machine sans-I/O), iotcom-ffi-support, iotcom-modbus, cdylib native
conformance/    test vector bersama yang dijalankan C# dan Rust
tests/          uji xUnit
samples/        sampel console dan sampel web IoTCom.Gateway
gallery/        Galeri IoTCom.Net (Avalonia) + perender screenshot headless
tools/          CLI iotcom
templates/      template dotnet new
notebooks/      Polyglot notebook (EN + ID)
docs/           dokumentasi (docs/en, docs/id) dan gambar
build/          cross-build native, gerbang docs/notebook, perkakas screenshot
```

## Proyek

- [PLAN.md](PLAN.md) — roadmap · [Progress.md](Progress.md) — pelacakan pengembangan
- [solution-design.md](solution-design.md) — dokumen desain solusi
- [CONTRIBUTING.md](CONTRIBUTING.md) · [SECURITY.md](SECURITY.md) · [CHANGELOG.md](CHANGELOG.md)
- Lisensi: [Apache-2.0](LICENSE)

---

<p align="center"><sub>IoTCom.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.</sub></p>
