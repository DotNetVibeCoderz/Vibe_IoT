# IoTCom.Net — Dokumen Desain Solusi

| | |
|---|---|
| **Status** | Draft v0.1 |
| **Tanggal** | 2026-10-04 |
| **Target runtime** | .NET 10 (LTS) untuk sisi managed, Rust (stable, edition 2021+) untuk sisi low-level |
| **Lisensi (usulan)** | Apache-2.0 (lihat bagian 15) |
| **Dokumen terkait** | Daftar protokol IoT & embedded per kategori (19 kategori) |

---

## 1. Ringkasan

**IoTCom.Net** adalah ekosistem library .NET untuk berkomunikasi dengan perangkat dan sistem IoT/embedded melalui banyak protokol, dengan peran **client / server** (request-response) maupun **publisher / consumer** (pub-sub), di Windows, Linux, macOS, dan Linux ARM (Raspberry Pi dan sejenisnya).

Tiga keputusan utama:

1. **Kurasi ketat.** Yang sudah ada di BCL atau pustaka resmi Microsoft **tidak dibuat ulang**. Yang sudah matang di ekosistem .NET **dibungkus lewat adapter**. Hanya celah yang nyata yang **diimplementasikan**.
2. **Rust untuk low-level.** Akses hardware, codec/state machine biner yang kompleks, dan protokol yang butuh keamanan memori dan fuzzing ditulis dengan Rust, lalu diekspos ke .NET lewat C ABI. API publik, DI, async, dan integrasi ASP.NET Core tetap C#.
3. **Satu paket pengalaman lengkap.** Library + CLI + sample (console, web, desktop) + **IoTCom.Net Gallery** (Avalonia) + notebook per protokol + template project + ekstensi VS Code + dokumentasi EN/ID.

---

## 2. Tujuan dan Non-Tujuan

### Tujuan
- API konsisten lintas protokol: `Client`, `Server`, `Publisher`, `Subscriber`, `Session`.
- Dua sisi komunikasi di setiap protokol bila memungkinkan: bisa jadi master **dan** slave, controller **dan** device, gateway **dan** simulator.
- Multiplatform dengan paket native per RID, kompatibel dengan trimming dan NativeAOT.
- Mudah dipelajari: setiap protokol punya sample, notebook, dan dokumentasi dua bahasa.
- Aman secara default: TLS bila tersedia, parser difuzz, tanpa `unsafe` yang tidak perlu di sisi C#.

### Non-Tujuan
- Tidak membuat ulang TCP/UDP/TLS/HTTP/WebSocket/gRPC/JSON/CBOR dan sejenisnya (sudah di BCL atau pustaka resmi).
- Tidak membuat driver sensor/aktuator (sudah ada **dotnet/iot**: `System.Device.Gpio` dan `Iot.Device.Bindings`).
- Tidak mengimplementasikan protokol layer radio/PHY (LoRa PHY, Zigbee radio, PLC modem, 5G). Library bicara ke gateway, modem, atau coprocessor lewat antarmuka serial/IP-nya.
- Bukan stack firmware. Fokusnya sisi host/gateway/cloud/tooling.
- Tidak menggantikan platform IoT (Azure IoT, AWS IoT, dsb.). IoTCom.Net berada di bawahnya.

---

## 3. Prinsip Desain

1. **Sans-I/O untuk protokol.** Logika protokol (parsing, state machine, timer) tidak melakukan I/O. Input byte masuk, event dan byte keluar. Transport (socket, serial, pipe) diatur oleh C#. Hasilnya: mudah diuji, deterministik, portabel, dan bisa dikompilasi ke WASM nanti.
2. **Rust hanya di tempat yang layak.** Lihat aturan keputusan di bagian 4.4. Jangan menyeberang FFI untuk fungsi kecil (CRC, COBS, SLIP): tulis di C#.
3. **Satu model, banyak protokol.** Abstraksi bersama di `IoTCom.Net.Abstractions`; detail protokol tetap terekspos (tidak dipaksa ke lowest common denominator).
4. **Async-first, zero-copy bila mungkin.** `System.IO.Pipelines`, `Memory<byte>`, `IAsyncEnumerable<T>`, `Channel<T>`.
5. **Observability bawaan.** `ILogger`, `ActivitySource`, `Meter` (OpenTelemetry-ready), plus *frame tap* untuk menangkap traffic mentah (dipakai Gallery dan VS Code).
6. **Trim/AOT-friendly.** `LibraryImport` (source-generated), tanpa reflection berat, source generator untuk codegen (mis. dialek MAVLink).
7. **Dokumentasi adalah bagian dari produk.** Build gagal bila halaman EN tidak punya pasangan ID (lihat bagian 12).

---

## 4. Kurasi Protokol

### 4.1 Tiga tier keputusan

| Tier | Arti | Tindakan |
|---|---|---|
| **T0 — BCL / resmi** | Sudah didukung .NET atau paket resmi Microsoft | **Tidak dibuat.** Hanya dipakai sebagai transport dan dibuatkan sample/cookbook |
| **T1 — Adapter** | Ada library .NET matang dan terawat | **Dibungkus** ke abstraksi IoTCom (opsional, paket terpisah). Tidak ditulis ulang |
| **T2 — Implementasi** | Celah nyata, atau butuh API terpadu client+server | **Dibuat**, di Rust (T2-R) atau C# managed (T2-C) |
| **T3 — Di luar cakupan** | Layer radio, OS, atau firmware | Hanya didokumentasikan cara berinteraksinya |

### 4.2 T0 — Tidak dibuat ulang (BCL dan resmi)

| Kebutuhan | Gunakan | Catatan |
|---|---|---|
| TCP, UDP, multicast, raw socket | `System.Net.Sockets` | Dasar semua transport IP |
| TLS | `SslStream` | DTLS **tidak** ada di BCL (celah, lihat T2) |
| QUIC | `System.Net.Quic` | Butuh `libmsquic` di Linux |
| HTTP/1.1, 2, 3, REST | `HttpClient`, Kestrel | |
| WebSocket | `ClientWebSocket`, ASP.NET Core | Dasar OCPP, MQTT over WS |
| Server-Sent Events | `System.Net.ServerSentEvents` | |
| gRPC | `Grpc.Net.Client`, `Grpc.AspNetCore` | Paket resmi, bukan BCL murni |
| DNS dasar, info NIC, IPv6 | `Dns`, `System.Net.NetworkInformation` | mDNS/DNS-SD tidak tercakup |
| Serial RS-232/422/485 | `System.IO.Ports` | Kontrol arah RS-485 terbatas (lihat T2 transport serial) |
| GPIO, I²C, SPI, PWM, 1-Wire, driver sensor | `System.Device.Gpio`, `Iot.Device.Bindings` | Perlu OS Linux/Windows IoT yang didukung |
| JSON, XML | `System.Text.Json`, `System.Xml` | |
| CBOR, ASN.1 | `System.Formats.Cbor`, `System.Formats.Asn1` | |
| Hash, CRC32/CRC64 | `System.IO.Hashing` | CRC16 dan varian lain **tidak ada**; dibuat katalog CRC |
| Kripto (AES-GCM/CCM, ECDH, HKDF, ChaCha20, X.509) | `System.Security.Cryptography` | Dasar OSCORE/LoRaWAN/DLMS di sisi C# |
| Buffer, pipeline, channel | `System.IO.Pipelines`, `System.Threading.Channels`, `BinaryPrimitives` | |
| JSON-RPC | `StreamJsonRpc` (Microsoft) | |

### 4.3 Matriks keputusan per kategori

Prioritas: **P0** fondasi, **P1** rilis awal, **P2** menyusul, **P3** opsional/kontribusi. Bahasa: **R** Rust, **C#** managed. Peran: **C** client, **S** server, **P** publisher, **Sub** subscriber.

#### Kategori 1–2: Bus on-board, debug, programming

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| UART, SPI, I²C, 1-Wire, GPIO | T0 | — | — | — | Pakai `System.IO.Ports` / `System.Device.Gpio` |
| I²S, PDM, SD/eMMC, PCIe, MIPI | T3 | — | — | — | Lapisan OS/driver |
| USB (bulk/interrupt/control), HID | T2-R | R (kandidat: `nusb`, `hidapi`) | C | **P1** | Transport dasar bagi DFU, CMSIS-DAP, adapter CAN USB |
| USB DFU | T2-R | R | C | P2 | Flash firmware via USB |
| SWD/JTAG, CMSIS-DAP, SWO, RTT | T2-R | R (kandidat: `probe-rs`) | C | P2 | Flash, baca memori, log RTT/defmt. Fitur pembeda utama |
| GDB Remote Serial Protocol | T2-C | C# | C | P3 | |
| PMBus/SMBus codec | T2-C | C# | C | P3 | Di atas `System.Device.Gpio` I²C |
| I³C, LIN, SENT, PSI5 | T3 / P3 | — | — | — | LIN via adapter serial (P3) |

#### Kategori 3: Industrial

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| Modbus RTU/ASCII/TCP | T2 | R (codec) + C# (I/O) | C, S | **P0** | API terpadu master dan slave, simulator bawaan. Library .NET lain ada, tetapi tidak punya server+client+simulator terpadu |
| OPC UA | T1 | C# | C, S, P, Sub | **P1** | Adapter ke `OPCFoundation.NetStandard`; tidak ditulis ulang |
| Sparkplug B | T2-C | C# (di atas MQTTnet + Protobuf) | P, Sub | **P1** | Payload, birth/death, state machine host app |
| CANopen (NMT, SDO, PDO, heartbeat) | T2-R | R | C, S | P1 | Di atas transport CAN |
| EtherCAT master | T2-R | R (kandidat: `ethercrab`) | C (master) | P2 | Butuh raw Ethernet + hak istimewa; dokumentasikan batasan real-time |
| EtherNet/IP (CIP explicit) | T2-R | R | C, S | P2 | Implicit I/O di P3 |
| PROFINET (DCP discovery, IO-controller dasar) | T2-R | R | C | P3 | Device-side sangat kompleks |
| HART-IP, IO-Link (via master gateway) | T2-C | C# | C | P3 | |
| MTConnect | T2-C | C# | C | P3 | HTTP/XML |
| TSN, Ethernet-APL, SPE | T3 | — | — | — | Lapisan PHY/OS |

#### Kategori 4: Otomotif dan transportasi

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| **CAN / CAN FD** (transport) | T2-R | R (kandidat: `socketcan`, driver PCAN/Kvaser/slcan/gs_usb) | C, S (send/receive) | **P0** | Abstraksi `ICanBus`; backend per OS |
| ISO-TP (ISO 15765-2) | T2-R | R | C, S | **P1** | |
| UDS (ISO 14229), OBD-II | T2-R | R | C (tester), S (ECU simulator) | **P1** | |
| SAE J1939, NMEA 2000, ISOBUS | T2-R | R | C, S | P1 / P2 / P3 | J1939 dulu; NMEA 2000 dan ISOBUS menumpang |
| DoIP | T2-C | C# | C | P2 | TCP/UDP biasa |
| XCP, CCP | T2-R | R | C | P3 | |
| SOME/IP | T2-R | R | C, S, P, Sub | P3 | Cek lisensi implementasi referensi |
| OCPP 1.6J / 2.0.1 | T2-C | C# (ASP.NET Core WebSocket) | C (charge point), S (CSMS) | P2 | |
| ISO 15118 | T3 / P3 | — | — | — | Sangat kompleks (V2G TLS, EXI); evaluasi nanti |
| FlexRay, MOST, CAN XL, V2X | T3 | — | — | — | Perangkat keras khusus |

#### Kategori 5: Dirgantara, drone, robotika, maritim

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| MAVLink v1/v2 | T2-R | R (codec + generator dialek; kandidat: crate `mavlink`) | C (GCS), S (vehicle sim), P, Sub | **P1** | Source generator C# untuk tipe pesan |
| NMEA 0183 (+AIS decode) | T2-C | C# | Sub, P | **P1** | Teks sederhana, tidak perlu Rust |
| UBX, RTCM 3 (GNSS) | T2-R | R | C, Sub | P2 | |
| DroneCAN/Cyphal | T2-R | R | P, Sub | P3 | Di atas transport CAN |
| CRSF, SBUS, MSP, Dynamixel | T2-C | C# | C | P3 | Serial |
| ROS 2 / DDS | T1 / T3 | — | — | — | Gunakan bridge (Zenoh/rosbridge) atau library DDS yang ada |
| MIL-STD-1553, ARINC 429/664, SpaceWire, CCSDS | T3 | — | — | — | Perangkat keras khusus |

#### Kategori 6–7: Nirkabel jarak dekat, smart home, building

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| **BLE** (GATT central, scan, notifikasi) | T2-R | R (kandidat: `btleplug`) | C | **P1** | BCL tidak punya; peripheral/GATT server terbatas per OS |
| Profil kesehatan BLE (HRP, GLP, BLP, HTP, dll.) | T2-C | C# | C | P2 | Di atas GATT |
| NFC/RFID (PC/SC, NDEF) | T1 + T2-C | C# | C | P2 | Adapter PC/SC; codec NDEF dibuat sendiri |
| Matter (controller/commissioner) | T2-R | R (kandidat: `rs-matter`) | C | P2 | Kompleksitas tinggi; mulai dari commissioning BLE/IP |
| KNXnet/IP (tunneling, routing) | T2-C | C# | C, S | P2 | UDP |
| BACnet/IP | T1 / T2-C | C# | C, S | P2 | Who-Is/I-Am, ReadProperty, COV. Evaluasi adapter dulu |
| **DMX512 (via adapter), Art-Net, sACN** | T2-C | C# | P, Sub | **P1** | UDP sederhana, demo visual bagus |
| Zigbee, Z-Wave, Thread (via coprocessor/NCP/RCP) | T2-C | C# | C | P3 | Serial API (EZSP, Z-Wave Serial API, Spinel) |
| IR remote (NEC, RC5, RC6, SIRC) | T2-C | C# | encode/decode | P3 | |
| Wi-Fi, ESP-NOW, UWB, Li-Fi, IrDA | T3 | — | — | — | OS/firmware |
| DALI | T3 / P3 | — | — | — | Lewat gateway |

#### Kategori 8–10: LPWAN, seluler, jaringan

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| **LoRaWAN** (MAC codec, join, MIC, enkripsi payload) | T2-R | R (kandidat: crate `lorawan`) | device simulator, network server ringan | **P1** | Plus Semtech UDP packet-forwarder protocol (C#) |
| **AT command engine** (3GPP TS 27.007 + vendor) | T2-C | C# | C | **P1** | Modul NB-IoT/LTE-M/GNSS via serial; parser URC |
| QMI/MBIM, PPP | T2-C | C# | C | P3 | |
| NB-IoT, LTE-M, 5G, Sigfox, Wi-SUN, mioty | T3 | — | — | — | Dipakai lewat MQTT/CoAP/LwM2M/HTTP di atasnya |
| 6LoWPAN, RPL, 6TiSCH, mesh | T3 | — | — | — | Border router dikontrol lewat OTBR REST/Spinel (P3) |

#### Kategori 11–13: Transport, framing, aplikasi, serialisasi

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| TCP, UDP, QUIC, TLS, HTTP, WebSocket, SSE, gRPC | T0 | — | — | — | |
| **DTLS 1.2/1.3** | T2-R | R | C, S | P2 | Celah BCL; untuk CoAP secure. Evaluasi crate yang ada |
| SLIP, COBS, HDLC, katalog CRC | T2-C | C# | codec | **P0** | Fungsi kecil, tulis di C#; test vector dibagi dengan Rust |
| **MQTT 3.1.1/5.0** (client + broker) | T1 | C# | C, S, P, Sub | **P0** | Adapter ke **MQTTnet**; bukan ditulis ulang |
| MQTT-SN | T2-C | C# | C, gateway | P2 | |
| **CoAP** (Observe, Block-wise, Link Format) | T2-R (codec) + C# (I/O) | R (kandidat: `coap-lite`) | C, S, Sub | **P1** | DTLS menyusul |
| AMQP 1.0, NATS, Kafka | T1 | C# | P, Sub | P2 | Adapter ke AMQPNetLite, NATS.Net, Confluent.Kafka |
| ZeroMQ | T1 | C# | P, Sub | P3 | NetMQ |
| **Zenoh** | T2-R | R (binding ke crate `zenoh`) | P, Sub, query | P2 | Rust-native, cocok sebagai pengganti DDS ringan |
| DDS/RTPS, DDS-XRCE | T1 / T3 | — | — | — | Adapter bila ada library .NET yang layak |
| oneM2M, W3C WoT (Thing Description) | T2-C | C# | — | P2 (WoT) / P3 | TD generator + validator |
| RTSP/RTP, ONVIF | T2-C | C# | C | P3 | |
| JSON, CBOR, ASN.1 | T0 | — | — | — | |
| Protobuf, MessagePack, FlatBuffers | T1 | C# | codec | P1 | Adapter ke Google.Protobuf, MessagePack-CSharp, Google.FlatBuffers |
| SenML, TLV helper | T2-C | C# | codec | P1 | SenML JSON/CBOR |

#### Kategori 14–15: Manajemen perangkat, keamanan

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| **LwM2M** (client dan server) | T2-C | C# (di atas CoAP IoTCom) | C, S | P2 | |
| SUIT manifest, tooling MCUboot | T2-R | R | parse/sign | P3 | |
| hawkBit DDI, Mender | T2-C | C# | C | P3 | HTTP |
| SNMP | T1 | C# | C, S | P3 | Adapter SharpSnmpLib |
| TR-069/369, NETCONF | T3 | — | — | — | |
| OSCORE, EDHOC | T2-R | R (kandidat: `lakers` untuk EDHOC) | — | P3 | Untuk CoAP aman |
| TLS, X.509, OAuth | T0 | — | — | — | |
| SPDM, DICE, TPM | T3 | — | — | — | |

#### Kategori 16–19: Energi, PLC, medis, waktu

| Protokol | Tier | Bahasa | Peran | Prioritas | Catatan |
|---|---|---|---|---|---|
| **DLMS/COSEM** (HDLC + APDU, GET/SET/ACTION, AES-GCM) | T2-R | R | C (reader), S (meter sim) | **P1** | OBIS catalog; optik/serial dan TCP |
| **M-Bus (wired)** | T2-R | R | C | **P1** | Telegram dan parsing record |
| Wireless M-Bus | T2-R | R | Sub | P3 | Via dongle |
| **IEC 60870-5-104** | T2-R | R | C, S | P2 | |
| DNP3, IEC 61850 (MMS, GOOSE) | T2-R | R | C, S | P3 | **Cek lisensi** crate yang ada (sebagian dual/non-komersial) sebelum dipakai |
| SunSpec (Modbus) | T2-C | C# | C | **P1** | Model JSON di atas IoTCom.Modbus |
| IEEE 2030.5, OpenADR | T2-C | C# | C, S | P3 | |
| PRIME, G3-PLC, G.hn | T3 | — | — | — | Lewat API modem |
| **HL7 v2 MLLP** | T2-C + T1 | C# | C, S | **P1** | Transport MLLP sendiri; parsing via adapter NHapi |
| **ASTM E1394 / LIS2-A2** | T2-C | C# | C, S | **P1** | Serial dan TCP; umum untuk analyzer lab |
| FHIR, DICOM | T1 | — | — | — | Pakai Firely SDK, fo-dicom; tidak dibuat ulang |
| IEEE 11073 (PHD/SDC), IHE PCD, POCT1-A | T2-C | C# | C | P3 | |
| NTP/SNTP | T2-C | C# | C | P2 | Klien kecil |
| PTP (IEEE 1588) | T2-R | R | C | P3 | Butuh hardware timestamping |
| **mDNS / DNS-SD** | T2-C | C# | C, S | **P1** | Discovery untuk Gallery dan CLI |
| SSDP/UPnP | T2-C | C# | C | P3 | |

### 4.4 Aturan keputusan: Rust atau C#

**Pilih Rust bila** salah satu terpenuhi:
- (a) Butuh akses OS/hardware yang tidak ada di BCL: raw CAN, USB, BLE, raw Ethernet, SWD/JTAG.
- (b) Codec/state machine biner kompleks yang layak difuzz dan dijaga memory-safety-nya: DLMS, IEC 104, MAVLink, LoRaWAN MAC, ISO-TP/UDS, CoAP.
- (c) Ada crate Rust matang dengan lisensi kompatibel yang bisa dibungkus.
- (d) Jalur panas (throughput tinggi, zero-copy).

**Pilih C# bila:**
- Protokol berbasis teks/JSON/HTTP/WebSocket (OCPP, WoT, hawkBit, ASTM, HL7 MLLP).
- UDP/TCP sederhana yang cukup dengan `System.Net.Sockets` (Art-Net, sACN, KNX, mDNS).
- Fungsi kecil bebas-state (CRC, COBS, SLIP): biaya FFI lebih mahal dari fungsinya.
- Adapter ke library .NET yang sudah ada.
- Lapisan API, DI, hosting, integrasi ASP.NET Core.

> **Catatan duplikasi.** Fungsi kecil yang dipakai di C# *dan* di crate Rust ditulis dua kali. Risiko divergensi ditekan dengan **test vector bersama** (`/conformance/*.json`) yang dijalankan oleh kedua sisi di CI.

---

## 5. Arsitektur

### 5.1 Lapisan

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Aplikasi: Console · Web (ASP.NET Core/Blazor) · Avalonia Gallery · Notebook  │
├──────────────────────────────────────────────────────────────────────────────┤
│ Tooling: iotcom CLI · Template · VS Code extension · Simulator               │
├──────────────────────────────────────────────────────────────────────────────┤
│ IoTCom.Net.Hosting / AspNetCore  (DI, BackgroundService, endpoint gateway)   │
├──────────────────────────────────────────────────────────────────────────────┤
│ API protokol (C#): Modbus · Can/Uds · Mavlink · Dlms · LoRaWAN · CoAP · ...  │
│ Adapter T1: MQTTnet · OPC UA · Kafka · NATS · AMQP · NHapi · ...             │
├──────────────────────────────────────────────────────────────────────────────┤
│ IoTCom.Net.Core: ITransport · Pipelines · Session · Timer · Telemetry · Tap  │
├───────────────────────────────┬──────────────────────────────────────────────┤
│ Transport C# (BCL)            │ Native bridge: LibraryImport · SafeHandle    │
│ TCP/UDP/TLS/WS/Serial/Mcast   │ Loader RID · ABI version check               │
├───────────────────────────────┴──────────────────────────────────────────────┤
│ Rust: iotcom-core (Machine sans-I/O) · codec · akses hardware                │
│ CAN · USB · BLE · EtherCAT · SWD/JTAG · DLMS · IEC104 · MAVLink · LoRaWAN    │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 Dua pola integrasi Rust

**Pola A — Sans-I/O (default untuk protokol).** Rust hanya berisi mesin protokol. C# menjalankan I/O dan memutar mesinnya.

```rust
// iotcom-core: kontrak umum, terinspirasi quinn-proto
pub trait Machine {
    type Event;
    type Command;

    fn handle_input(&mut self, now: Instant, bytes: &[u8]) -> Result<(), Error>;
    fn handle_command(&mut self, now: Instant, cmd: Self::Command) -> Result<(), Error>;
    fn handle_timeout(&mut self, now: Instant);

    fn poll_transmit(&mut self, out: &mut Vec<u8>) -> Option<Transmit>;
    fn poll_event(&mut self) -> Option<Self::Event>;
    fn poll_timeout(&self) -> Option<Instant>;
}
```

Keuntungan: transport apa pun (TCP, serial, TLS, WebSocket, pipe, replay file) bekerja tanpa perubahan di Rust; tes deterministik; tidak perlu runtime async di sisi Rust; surface FFI sempit.

**Pola B — Rust memiliki I/O (hanya bila terpaksa).** Untuk CAN, USB, BLE, EtherCAT, SWD: Rust membuka perangkat dan mendorong event ke C# lewat callback ke `Channel<T>` terbatas (backpressure), atau lewat polling di thread khusus.

### 5.3 Kontrak FFI (C ABI)

Aturan wajib:
- Semua fungsi `extern "C"`, tanpa panic melintasi batas (`catch_unwind` di setiap entry point, panic menjadi status error).
- Handle buram (`*mut Opaque`), dibungkus `SafeHandle` di C#. Satu fungsi `*_free` per tipe.
- Status berupa `i32` enum (`IOTCOM_OK`, `IOTCOM_ERR_*`) + `iotcom_last_error(buf, len)` per thread.
- Buffer dimiliki pemanggil; Rust tidak mengembalikan pointer yang harus di-free C# kecuali lewat API `*_free` yang eksplisit.
- Event berupa struct `#[repr(C)]` bertag; panjang variabel lewat pasangan (ptr, len) dengan masa hidup dibatasi sampai pemanggilan berikutnya.
- `iotcom_abi_version()` diperiksa saat load; mismatch menghasilkan exception yang jelas.
- Binding C# dihasilkan otomatis (kandidat: `cbindgen` + `csbindgen`; alternatif `uniffi-bindgen-cs`), diperiksa diff-nya di CI.

```rust
#[no_mangle]
pub extern "C" fn iotcom_modbus_master_new(
    cfg: *const ModbusMasterCfg,
    out: *mut *mut ModbusMaster,
) -> i32 { ffi_guard(|| { /* ... */ Ok(()) }) }
```

```csharp
[LibraryImport("iotcom_modbus", EntryPoint = "iotcom_modbus_master_new")]
internal static partial int MasterNew(in ModbusMasterCfg cfg, out nint handle);
```

### 5.4 Abstraksi C# (kontrak publik)

```csharp
namespace IoTCom.Net;

public interface IEndpoint : IAsyncDisposable
{
    string Protocol { get; }
    EndpointState State { get; }
    event EventHandler<StateChangedEventArgs>? StateChanged;
}

public interface IClientEndpoint : IEndpoint
{
    ValueTask ConnectAsync(CancellationToken ct = default);
    ValueTask DisconnectAsync(CancellationToken ct = default);
}

public interface IServerEndpoint : IEndpoint
{
    ValueTask StartAsync(CancellationToken ct = default);
    ValueTask StopAsync(CancellationToken ct = default);
}

public interface IPublisher<in T>
{
    ValueTask PublishAsync(string topic, T message, PublishOptions? options = null, CancellationToken ct = default);
}

public interface ISubscriber<T>
{
    IAsyncEnumerable<Message<T>> SubscribeAsync(string filter, CancellationToken ct = default);
}

public interface ITransport : IAsyncDisposable
{
    IDuplexPipe Pipe { get; }                 // System.IO.Pipelines
    TransportInfo Info { get; }
    ValueTask OpenAsync(CancellationToken ct);
}
```

Prinsip: abstraksi bersama dipakai bila cocok (MQTT, CoAP Observe, Zenoh, Sparkplug, MAVLink, NMEA = pub/sub). Protokol request-response (Modbus, UDS, DLMS) mengekspos API spesifik sendiri dan hanya mengimplementasikan `IClientEndpoint`/`IServerEndpoint`.

**Contoh penggunaan (target ergonomi):**

```csharp
// Modbus master (client)
await using var master = ModbusClient.Create(o => o
    .UseTcp("192.168.1.10", 502)
    .WithUnitId(1)
    .WithTimeout(TimeSpan.FromSeconds(2)));
await master.ConnectAsync();
ushort[] regs = await master.ReadHoldingRegistersAsync(address: 0, count: 10);

// Modbus slave (server) + simulator
await using var slave = ModbusServer.Create(o => o.UseTcp(IPAddress.Any, 1502));
slave.Map.HoldingRegisters[0] = 230;
await slave.StartAsync();

// MAVLink: consumer telemetri
await using var link = MavlinkLink.Create(o => o.UseUdp(listenPort: 14550));
await foreach (var msg in link.SubscribeAsync<Heartbeat>())
    Console.WriteLine($"{msg.Payload.Type} mode={msg.Payload.CustomMode}");

// CAN + UDS tester
await using var bus = CanBus.Open("socketcan:can0", bitrate: 500_000);
await using var uds = UdsClient.Create(bus, txId: 0x7E0, rxId: 0x7E8);
var vin = await uds.ReadDataByIdentifierAsync(0xF190);
```

### 5.5 Hosting, DI, dan observability

- `services.AddIoTCom(b => b.AddModbusClient("plc1", ...).AddMqtt(...))` dengan `Microsoft.Extensions.Hosting`.
- Health check per endpoint (`IHealthCheck`), konfigurasi via `IOptions<T>` dan `appsettings.json` (skema JSON dipublikasi untuk VS Code).
- Telemetry: `ActivitySource("IoTCom.Net")`, `Meter("IoTCom.Net")` (frame in/out, error, latensi, reconnect).
- **Frame tap:** `ITrafficTap` menerima setiap frame mentah beserta arah, timestamp, dan hasil decode. Dipakai oleh Gallery (Protocol Inspector), CLI (`iotcom sniff`), VS Code (viewer), dan export PCAP/pcapng untuk protokol IP.

### 5.6 Model error dan resiliensi

- Hierarki exception: `IoTComException` → `TransportException`, `ProtocolException`, `TimeoutException`, `DeviceException` (kode vendor/spesifik protokol dipertahankan).
- Reconnect dengan backoff (opsional via `Polly`-style policy internal, tanpa dependensi wajib).
- Semua operasi menerima `CancellationToken`; tidak ada blocking call tersembunyi.

### 5.7 Threading dan performa

- Satu *driver loop* per endpoint (Pola A): `PipeReader` → `Machine.handle_input` → event → `Channel`.
- Pemanggilan FFI dibatch (satu panggilan memproses banyak byte/event) untuk menekan biaya lintas batas.
- Target awal (diukur, bukan janji): Modbus TCP ≥ 20k req/s per koneksi pada loopback; decode MAVLink ≥ 500k pesan/s per core.

---

## 6. Struktur Repositori dan Paket

```
IoTCom.Net/
├─ src/                         # C# library
│  ├─ IoTCom.Net.Abstractions/
│  ├─ IoTCom.Net.Core/          # transport, pipelines, loader native, telemetry
│  ├─ IoTCom.Net.Hosting/
│  ├─ IoTCom.Net.AspNetCore/
│  ├─ IoTCom.Net.Transport.Serial|Can|Usb|Ble|RawEthernet/
│  ├─ IoTCom.Net.Protocols.Modbus|CanOpen|IsoTp|Uds|J1939|Mavlink|Dlms|MBus|Iec104|
│  │                       LoRaWAN|CoAP|Lwm2m|Sparkplug|ArtNet|Dmx|Knx|Nmea|Hl7|Astm|AtCommand|...
│  ├─ IoTCom.Net.Adapters.Mqtt|OpcUa|Kafka|Nats|Amqp|Zenoh|...
│  ├─ IoTCom.Net.Serialization.SenML|Tlv|...
│  ├─ IoTCom.Net.Simulators/    # perangkat virtual untuk sample, notebook, test
│  └─ IoTCom.Net.Testing/
├─ rust/                        # workspace Rust
│  ├─ Cargo.toml
│  └─ crates/
│     ├─ iotcom-core/           # trait Machine, error, waktu, buffer
│     ├─ iotcom-ffi-support/    # ffi_guard, handle, last_error
│     ├─ iotcom-modbus/  iotcom-can/  iotcom-isotp-uds/  iotcom-canopen/
│     ├─ iotcom-mavlink/ iotcom-dlms/ iotcom-mbus/ iotcom-iec104/
│     ├─ iotcom-lorawan/ iotcom-coap/ iotcom-usb/ iotcom-ble/ iotcom-debug/ ...
│     └─ native/                # satu crate cdylib per paket NuGet protokol
├─ conformance/                 # test vector JSON bersama C# dan Rust
├─ samples/
│  ├─ console/   ├─ web/   └─ desktop/
├─ gallery/                     # IoTCom.Net Gallery (Avalonia)
├─ notebooks/                   # .NET Interactive / Polyglot Notebooks
├─ templates/                   # dotnet new templates
├─ tools/iotcom-cli/            # dotnet tool
├─ vscode-extension/            # TypeScript
├─ docs/
│  ├─ en/
│  └─ id/
├─ tests/ · benchmarks/ · build/ · .github/workflows/
├─ Directory.Build.props · Directory.Packages.props (Central Package Management)
└─ README.md · README.id.md · CONTRIBUTING.md · SECURITY.md · LICENSE
```

**Paket NuGet (ringkas):** `IoTCom.Net.Abstractions`, `IoTCom.Net.Core`, `IoTCom.Net.Hosting`, `IoTCom.Net.AspNetCore`, satu paket per protokol (`IoTCom.Net.Protocols.*`), satu paket per adapter (`IoTCom.Net.Adapters.*`), meta-paket `IoTCom.Net` (set umum), `IoTCom.Net.Templates`, dan dotnet tool `iotcom`.

---

## 7. Platform dan Packaging Native

### 7.1 Matriks dukungan

| Platform | RID | Status target | Catatan |
|---|---|---|---|
| Windows x64 / arm64 | `win-x64`, `win-arm64` | Tier 1 | CAN via driver vendor (PCAN, Kvaser); BLE via WinRT |
| Linux x64 / arm64 | `linux-x64`, `linux-arm64` | Tier 1 | SocketCAN, BlueZ, raw socket |
| Linux ARM 32-bit (Raspberry Pi lama) | `linux-arm` | Tier 1 | |
| Linux musl (Alpine, container) | `linux-musl-x64`, `linux-musl-arm64` | Tier 1 | |
| macOS Intel / Apple Silicon | `osx-x64`, `osx-arm64` | Tier 2 | Tanpa SocketCAN; adapter USB-CAN tetap bisa |
| Android | `android-arm64` | Tier 3 | Subset (TCP/UDP protokol, BLE, USB host) |
| iOS | — | Di luar rilis awal | Subset pure-network bisa menyusul |
| Browser (Blazor WASM) | `browser-wasm` | Rencana jangka panjang | Mesin sans-I/O Rust dikompilasi ke `wasm32`; transport lewat WebSocket |

### 7.2 Distribusi native
- Native library ditaruh di `runtimes/{rid}/native/` pada paket NuGet; resolusi lewat `NativeLibrary.SetDllImportResolver` + fallback ke path aplikasi.
- Cross-compile: `cargo-zigbuild` (target glibc lama untuk kompatibilitas distro) dan `cross`; macOS dibuild di runner macOS.
- Satu cdylib per paket protokol (lihat ADR-003) dengan `iotcom-core` ter-link statis.
- Tanda tangan dan notarization untuk macOS/Windows pada rilis.
- Dokumentasikan hak yang dibutuhkan: `CAP_NET_RAW` (EtherCAT, GOOSE), grup `dialout`/`can`, udev rules USB, izin Bluetooth.

### 7.3 Target framework
- `net10.0` sebagai target utama. Bila ada permintaan kuat, `Abstractions` dan `Core` dapat multi-target `net8.0`.
- `IsTrimmable` dan `IsAotCompatible` aktif; analyzer dijalankan di CI.

---

## 8. Sample App

### 8.1 Console (`samples/console`)
Satu proyek kecil per skenario, ≤150 baris, dapat dijalankan dengan simulator bawaan (`--simulate`) bila perangkat tidak ada:

`ModbusMaster`, `ModbusSlaveSimulator`, `CanSniffer`, `UdsTester`, `ObdDashboardCli`, `MavlinkTelemetry`, `NmeaGpsReader`, `LoRaWanGatewayMonitor`, `DlmsMeterReader`, `MBusScanner`, `CoapObserve`, `Lwm2mClient`, `SparkplugEdgeNode`, `ArtNetPlayer`, `Hl7MllpListener`, `AstmAnalyzerBridge`, `MdnsBrowser`, `ProbeFlash`.

### 8.2 Web (`samples/web`)
| Sample | Isi |
|---|---|
| **IoTCom.Gateway** | ASP.NET Core: bridge Modbus/CAN/DLMS → MQTT/HTTP/SignalR; konfigurasi via `appsettings.json`; OpenTelemetry; Docker Compose bersama simulator |
| **Live Dashboard** | Blazor (Interactive Server) membaca data gateway, grafik real-time, status endpoint |
| **OCPP CSMS mini** | Server OCPP 2.0.1 dengan UI daftar charge point |
| **Lab Interface** | Penerima HL7/ASTM dari analyzer, tampilkan hasil dan ekspor FHIR (lewat Firely SDK) |

### 8.3 Desktop: **IoTCom.Net Gallery** (`gallery/`, Avalonia)

Aplikasi pembelajaran dan demonstrasi, mirip galeri komponen: pilih use case → jalankan → lihat kode.

**Fitur inti**
- Navigasi per kategori (selaras 19 kategori dokumen protokol) dan per use case.
- **Setiap demo punya tab:** *Run* (UI interaktif), *Code* (sumber C# dengan syntax highlight, diambil dari file yang sama dengan yang dikompilasi), *Docs* (ringkasan + tautan ke `docs/`), *Traffic* (Protocol Inspector: hex dump + pohon decode dari frame tap).
- Simulator in-process, jadi seluruh demo berjalan tanpa hardware; mode *Real device* memilih port/alamat sebenarnya.
- Tema terang/gelap, **bahasa EN/ID** (resource `.resx`/`.axaml` terlokalisasi, bisa diganti saat runtime).
- Arsitektur plugin: setiap demo mengimplementasikan `IGalleryDemo` dengan metadata (judul EN/ID, tag protokol, tingkat kesulitan, dependensi hardware).
- MVVM dengan `CommunityToolkit.Mvvm`; platform: Windows, Linux, macOS.

**Katalog use case awal (kandidat)**

| # | Use case | Protokol | Isi demo |
|---|---|---|---|
| 1 | Pabrik pintar | Modbus, OPC UA, Sparkplug B, MQTT | Dashboard PLC virtual, tag browser, alarm |
| 2 | Diagnostik kendaraan | CAN, ISO-TP, UDS, OBD-II | Baca DTC, VIN, live data, ECU simulator |
| 3 | Ground control drone | MAVLink | HUD sederhana, parameter, misi |
| 4 | Pembacaan smart meter | DLMS/COSEM, M-Bus, SunSpec | Baca OBIS, profil beban, grafik konsumsi |
| 5 | Gardu dan SCADA | IEC 104, DNP3 (bila lisensi lolos), Modbus | Master dan outstation simulator |
| 6 | Rumah dan gedung pintar | Matter, KNX, BACnet, mDNS | Discovery dan kontrol perangkat |
| 7 | Pencahayaan panggung | Art-Net, sACN, DMX | Fader grid, efek, pemetaan pixel |
| 8 | Monitor jaringan LoRaWAN | LoRaWAN, Semtech UDP | Gateway virtual, join, uplink/downlink |
| 9 | Pelacak GNSS dan maritim | NMEA 0183/2000, UBX, RTCM, AIS | Peta posisi, satelit, kapal sekitar |
| 10 | Antarmuka perangkat medis/lab | HL7 v2 MLLP, ASTM, BLE health | Terima hasil, kirim order, monitor vital |
| 11 | Stasiun pengisian EV | OCPP, ISO 15118 (P3) | Charge point + CSMS simulator |
| 12 | Probe dan flash firmware | SWD/JTAG, DFU, RTT | Flash, baca memori, log RTT |
| 13 | Manajemen perangkat | LwM2M, CoAP, SUIT (P3) | Objek LwM2M, observe, update |
| 14 | Modul seluler | AT command | Terminal AT, parser URC, status modem |
| 15 | Pembaca NFC/RFID | PC/SC, NDEF | Baca/tulis tag |
| 16 | **Protocol Workbench** | Semua | Sniffer, decoder, simulator generik, replay capture |
| 17 | Pub/Sub multi-broker | MQTT, NATS, Zenoh, Kafka | Perbandingan latensi, bridge antar broker |

---

## 9. Notebook per Protokol

- Format: **.NET Interactive / Polyglot Notebooks** (`.ipynb` atau `.dib`) di `notebooks/`, dibuka di VS Code dan Jupyter.
- Struktur seragam per notebook: *Apa itu protokol ini → Setup (`#r "nuget: IoTCom.Net.Protocols.X"`) → Client → Server → Pub/Sub (bila relevan) → Eksperimen dengan simulator → Troubleshooting → Latihan*.
- Penamaan: `notebooks/{kategori}/{NN}-{protokol}.{en|id}.ipynb`. Seluruh teks naratif tersedia EN dan ID; kode identik.
- Notebook berjalan penuh melawan simulator in-process, tanpa hardware.
- Notebook "peta jalan": `00-start-here` (instalasi, struktur API) dan `99-protocol-chooser` (pohon keputusan memilih protokol).
- CI mengeksekusi notebook secara headless (mis. dengan `dotnet-repl`) agar contoh tidak membusuk.

---

## 10. Template Project (`dotnet new`)

Paket `IoTCom.Net.Templates`:

| Short name | Isi |
|---|---|
| `iotcom-console` | Console app dengan satu endpoint, logging, config |
| `iotcom-worker` | Worker Service (BackgroundService) sebagai gateway edge, siap systemd/Windows Service/Docker |
| `iotcom-gateway-web` | ASP.NET Core minimal API + SignalR + OpenTelemetry + Dockerfile |
| `iotcom-dashboard` | Blazor dashboard terhubung ke gateway |
| `iotcom-avalonia` | Aplikasi desktop Avalonia dengan MVVM dan Protocol Inspector siap pakai |
| `iotcom-simulator` | Proyek simulator perangkat virtual |
| `iotcom-protocol-plugin` | Kerangka protokol kustom: solution C# + crate Rust sans-I/O + FFI + test + notebook |
| `iotcom-notebook` | Notebook starter EN/ID |

Opsi template: `--protocol modbus|can|mavlink|...`, `--transport tcp|serial|can`, `--lang en|id` (bahasa komentar dan README hasil), `--aot`, `--docker`.

---

## 11. Ekstensi VS Code

Nama: **IoTCom.Net Tools** (TypeScript). Berkomunikasi dengan `iotcom` CLI lewat stdio (JSON-RPC), sehingga logika tidak diduplikasi.

| Fitur | Detail |
|---|---|
| Scaffolding | Wizard "New IoTCom project" (memanggil `dotnet new`) |
| Protocol Explorer | Tree view protokol → paket NuGet, contoh, dokumentasi, notebook |
| Snippet | Snippet C# per protokol (client, server, pub/sub) |
| Device Explorer | Daftar serial port, CAN interface, perangkat USB, BLE, hasil mDNS |
| Frame/Hex Viewer | Webview: tempel/buka byte → decode per protokol (pohon field) |
| Traffic Monitor | Sniffer langsung via CLI; filter; ekspor pcapng/CSV |
| Simulator | Start/stop simulator dari panel; status di status bar |
| Config assist | JSON Schema untuk `iotcom.json` / `appsettings.json` (autocomplete, validasi) |
| Notebook helper | Buka notebook protokol terkait dari file kode |
| Dokumentasi | Hover/Quick link ke halaman docs EN/ID (mengikuti bahasa UI VS Code) |
| i18n | Teks ekstensi EN dan ID |

Rilis di Visual Studio Marketplace dan Open VSX. Pengujian dengan `@vscode/test-electron`.

---

## 12. Dokumentasi (EN + ID)

```
docs/
├─ en/
│  ├─ index.md
│  ├─ getting-started/        # install, quickstart, platform & permission
│  ├─ concepts/               # sans-I/O, roles, transport, frame tap, error model
│  ├─ protocols/              # satu halaman per protokol (template baku)
│  ├─ guides/                 # gateway, simulator, deployment, AOT, security
│  ├─ gallery/ · notebooks/ · templates/ · vscode/
│  ├─ native/                 # arsitektur Rust, FFI, build dari source
│  ├─ contributing/           # menambah protokol baru (end-to-end)
│  ├─ reference/              # API (DocFX), CLI, config schema
│  └─ glossary.md
└─ id/                        # struktur identik, bahasa Indonesia
```

**Template halaman protokol** (wajib identik EN/ID): ringkasan → kapan dipakai → matriks peran (client/server/pub/sub) → transport yang didukung → instalasi → quickstart → opsi konfigurasi → contoh kode → simulator → pengujian dan interoperabilitas → keamanan → keterbatasan → tautan notebook dan Gallery.

**Kebijakan terjemahan**
- EN adalah sumber; ID diterjemahkan manusia dan ditinjau (bukan sekadar mesin).
- Istilah teknis baku dipertahankan (mis. *publisher*, *frame*, *payload*), dengan glosarium EN↔ID.
- CI memeriksa **paritas**: setiap `docs/en/**/*.md` harus punya `docs/id/**/*.md`; selisih menggagalkan build. Penanda `translation-status` di front-matter (`synced` / `outdated`) menampilkan lencana di situs.
- Situs dokumentasi: DocFX (referensi API) + Markdown, pengalih bahasa EN/ID.
- README: `README.md` (EN) dan `README.id.md` (ID) di root dan di setiap proyek sample.

---

## 13. Strategi Pengujian

| Lapisan | Pendekatan |
|---|---|
| Rust unit/property | `cargo test`, `proptest`; target cakupan tinggi pada parser |
| Rust fuzzing | `cargo-fuzz` untuk setiap `handle_input` (corpus dari capture nyata); dijalankan terjadwal di CI |
| Conformance | Test vector bersama (`/conformance`) dijalankan di Rust **dan** C# |
| C# unit | xUnit, `IoTCom.Net.Testing` (transport in-memory, jam virtual) |
| Integrasi | Client ↔ server IoTCom sendiri, client ↔ simulator pihak ketiga (mis. broker/OPC UA server uji) dalam container |
| Replay | Capture pcap/log nyata sebagai golden test |
| Hardware-in-the-loop | Opsional, runner khusus (adapter CAN, USB-serial, dongle BLE); ditandai kategori terpisah |
| Lintas platform | Matriks CI: Windows, Ubuntu (glibc), Alpine (musl), macOS, linux-arm64 (QEMU/runner ARM) |
| Performa | BenchmarkDotNet (C#), `criterion` (Rust); regresi dipantau |
| Notebook dan sample | Dieksekusi di CI agar tetap valid |
| FFI | Uji ABI (ukuran/alignment struct), uji leak (sanitizer Rust + `dotnet-counters`/stress loop handle) |

---

## 14. CI/CD dan Rilis

1. **Build Rust** per RID (matriks) → artefak native.
2. **Build dan test C#** dengan artefak native.
3. **Pack NuGet** (SourceLink, deterministic build, symbol package, SBOM CycloneDX).
4. **Build** Gallery (installer/portable per OS), ekstensi VSIX, situs docs.
5. **Gerbang kualitas:** paritas docs EN/ID, notebook run, analyzer trimming/AOT, `cargo clippy -D warnings`, `cargo deny`/`cargo audit`.
6. Versi: **SemVer**; versi ABI native dipisah dan dicek runtime. Rilis lewat tag; changelog EN/ID.

---

## 15. Keamanan dan Lisensi

**Keamanan**
- Input dari jaringan/perangkat dianggap tidak tepercaya: semua parser berbatas panjang, tanpa alokasi tak terbatas, difuzz.
- Default aman: TLS/DTLS bila protokol mendukung; kredensial tidak di-log; frame tap menyembunyikan field sensitif secara default.
- Rantai pasok: `cargo deny`, `cargo audit`, `dotnet list package --vulnerable`, pin versi, SBOM, tanda tangan paket, build reproducible bila memungkinkan.
- Modul yang bisa mengubah perangkat fisik (Modbus write, UDS write/security access, SWD flash, OCPP remote start) punya mode **read-only** dan konfirmasi eksplisit di CLI/Gallery. Peringatan keselamatan dicantumkan di dokumentasi industrial dan otomotif.
- `SECURITY.md` dengan jalur pelaporan kerentanan.

**Lisensi**
- Usulan: **Apache-2.0** (hak paten eksplisit; cocok untuk adopsi enterprise).
- **Audit lisensi per dependensi** sebelum menambahkan crate/library. Beberapa implementasi protokol industrial dilisensikan dual atau non-komersial (mis. stack DNP3/IEC 61850 tertentu) dan implementasi referensi seperti SOEM/vsomeip punya kewajiban khusus; ini menjadi syarat gerbang untuk protokol P3 terkait.
- Larang dependensi GPL/AGPL pada paket inti.

---

## 16. Roadmap

Estimasi indikatif untuk tim kecil (2–4 orang); akan direvisi setelah Fase 0.

| Fase | Fokus | Keluaran utama |
|---|---|---|
| **0 — Fondasi** (±4–6 minggu) | Repo, CI matriks, `iotcom-core`, kontrak FFI + `ffi_guard`, native loader, `Abstractions`/`Core`, frame tap, SLIP/COBS/CRC, Modbus (master+slave+simulator), adapter MQTT, CLI dasar, docs skeleton EN/ID, template `iotcom-console` | Paket alpha; Modbus end-to-end; 3 notebook; 5 sample console |
| **1 — Rilis awal** (±8–12 minggu) | CAN (SocketCAN + 1 adapter USB), ISO-TP/UDS/OBD-II, CoAP, MAVLink, LoRaWAN, DLMS + M-Bus, NMEA, Art-Net/sACN, mDNS, AT engine, HL7 MLLP + ASTM, Sparkplug B, OPC UA adapter, BLE central, USB transport | v0.5 beta; Gallery dengan ≥10 demo; VS Code extension v0.1; template lengkap |
| **2 — Perluasan** (±12 minggu) | CANopen, J1939, IEC 104, EtherNet/IP, EtherCAT, SWD/DFU, Matter controller, KNX, BACnet, LwM2M, Zenoh, DTLS, OCPP, NTP, NFC/NDEF, adapter Kafka/NATS/AMQP | v1.0; dokumentasi lengkap EN/ID; Gallery 17 use case |
| **3 — Opsional** | PROFINET, DNP3/IEC 61850 (jika lisensi lolos), SOME/IP, XCP, SUIT/MCUboot, OSCORE/EDHOC, PTP, ISO 15118, WASM build | Berdasarkan kebutuhan dan kontribusi komunitas |

**Definition of Done per protokol:** API publik terdokumentasi (XML doc + halaman EN/ID) · unit + conformance + fuzz (bila Rust) · simulator · sample console · notebook · demo Gallery (bila relevan) · benchmark dasar · entri di matriks dukungan.

---

## 17. Risiko dan Mitigasi

| Risiko | Dampak | Mitigasi |
|---|---|---|
| Kompleksitas build native multi-RID | Rilis tersendat | Otomasi penuh di CI sejak Fase 0; matriks kecil dulu (win-x64, linux-x64, linux-arm64, osx-arm64), tambah bertahap |
| Bug di batas FFI (leak, panic, ABI drift) | Crash proses | `ffi_guard`, SafeHandle, versi ABI, binding ter-generate, uji stress |
| Dukungan hardware beragam (adapter CAN, BLE) | Beban dukungan besar | Mulai dengan SocketCAN + 1–2 adapter populer; daftar perangkat yang diuji secara publik |
| Lisensi dependensi | Blokir distribusi | Audit sejak awal; ganti dengan implementasi sendiri bila perlu |
| Lingkup terlalu luas | Banyak protokol setengah jadi | Prioritas P0–P3 ketat; Definition of Done; protokol P3 menunggu kontributor |
| Keselamatan fisik (menulis ke PLC/ECU/charger) | Risiko ke dunia nyata | Mode read-only default, konfirmasi, peringatan di docs, tidak ada operasi destruktif tanpa opt-in |
| Dokumentasi EN/ID tidak sinkron | Kualitas docs turun | Gerbang paritas di CI, status terjemahan, glosarium |
| Perbedaan perilaku antar OS (serial, BLE, raw socket) | Bug sulit direproduksi | Matriks CI nyata, dokumentasi batasan per platform, abstraksi `TransportInfo` |
| Ketergantungan pada crate Rust pihak ketiga yang berubah | Breaking change | Pin versi, wrapper tipis, `cargo vendor` untuk rilis |

---

## 18. Keputusan Arsitektur (ADR ringkas)

| ID | Keputusan | Alasan | Konsekuensi |
|---|---|---|---|
| ADR-001 | Rust untuk low-level, C# untuk API publik | Keamanan memori, fuzzing, ekosistem crate (probe-rs, ethercrab, btleplug), performa | Dua toolchain; perlu disiplin FFI |
| ADR-002 | Protokol dirancang sans-I/O | Testability, portabilitas (WASM), surface FFI kecil | Perlu driver loop di C# untuk tiap protokol |
| ADR-003 | Satu cdylib per paket protokol, bukan satu monolit | Aplikasi hanya membawa yang dipakai; versi mandiri | Ukuran total bila banyak paket; tinjau ulang bila >10 paket umum dipakai bersama |
| ADR-004 | C ABI + binding ter-generate (bukan C++/COM) | Stabil, didukung `LibraryImport`, AOT-friendly | Struct harus `repr(C)`; tanpa tipe generik lintas batas |
| ADR-005 | Adapter untuk library .NET matang (MQTTnet, OPC UA, NHapi, dst.) | Hindari duplikasi dan beban pemeliharaan | Bergantung pada kesehatan proyek upstream |
| ADR-006 | Fungsi kecil tanpa state ditulis di C# | Biaya FFI > manfaat | Duplikasi dengan sisi Rust; diatasi test vector bersama |
| ADR-007 | Docs EN sebagai sumber, ID terjemahan wajib dengan gerbang CI | Dua bahasa terjaga | Menambah effort rilis |
| ADR-008 | Gallery memakai Avalonia + MVVM + plugin demo | Lintas OS, UI modern, mudah ditambah demo | Android/browser tidak mendukung semua demo (butuh native) |

---

## 19. Pertanyaan Terbuka

1. Daftar akhir **adapter CAN** yang didukung pertama (PCAN, Kvaser, slcan, gs_usb/candleLight, Vector)?
2. **Matter**: cukup *controller* atau juga *device* (bridge)? Menentukan kompleksitas P2.
3. **BACnet** dan **OPC UA**: murni adapter, atau perlu API server sendiri?
4. **Lisensi** final (Apache-2.0 vs MIT) dan kebijakan dual-license untuk modul industrial bila diperlukan.
5. Dukungan **Android/iOS** sebagai target resmi atau cukup eksperimen komunitas?
6. Perlukah **managed fallback** (C# murni) untuk platform tanpa native (browser, iOS) pada beberapa protokol sederhana?
7. Model **rilis Gallery**: installer, portable, atau Microsoft Store/Flatpak/Homebrew?
8. Target **hardware referensi** untuk uji HIL (Raspberry Pi + HAT CAN, adapter USB-RS485, dongle BLE) dan siapa yang menjalankannya.

---

## Lampiran A — Pemetaan 19 Kategori ke Paket

| # | Kategori | Paket utama |
|---|---|---|
| 1 | Bus on-board | T0 (`System.Device.Gpio`, `System.IO.Ports`) + `Transport.Usb` |
| 2 | Debug/programming | `IoTCom.Net.Debug` (SWD/JTAG, DFU, RTT) |
| 3 | Industrial | `Protocols.Modbus`, `CanOpen`, `EtherNetIp`, `EtherCat`, `Adapters.OpcUa`, `Sparkplug` |
| 4 | Otomotif | `Transport.Can`, `IsoTp`, `Uds`, `J1939`, `DoIp`, `Ocpp` |
| 5 | Dirgantara/drone/maritim | `Mavlink`, `Nmea`, `Ubx`, `Rtcm` |
| 6 | Nirkabel jarak dekat | `Transport.Ble`, `Nfc`, `Zigbee`/`ZWave` (via coprocessor) |
| 7 | Smart home/building | `Matter`, `Knx`, `Bacnet`, `Dmx`, `ArtNet`, `Sacn` |
| 8 | LPWAN | `LoRaWAN` |
| 9 | Seluler/satelit | `AtCommand` (+ LwM2M/CoAP di atas) |
| 10 | Jaringan/routing | T3 (dokumentasi interaksi) |
| 11 | Transport/framing | T0 + `Framing` (SLIP/COBS/HDLC/CRC), `Dtls` |
| 12 | Aplikasi/messaging | `Adapters.Mqtt/Nats/Amqp/Kafka/Zenoh`, `CoAP`, `MqttSn`, `Wot` |
| 13 | Serialisasi | T0 + `Serialization.SenML/Tlv` + adapter Protobuf/MessagePack |
| 14 | Manajemen perangkat | `Lwm2m`, `Suit`, `HawkBit` |
| 15 | Keamanan | T0 + `Oscore`/`Edhoc` (P3) |
| 16 | Smart grid/metering | `Dlms`, `MBus`, `Iec104`, `SunSpec`, `Dnp3`/`Iec61850` (P3) |
| 17 | Power line | T3 |
| 18 | Medis | `Hl7` (MLLP), `Astm`, `BleHealth`, adapter FHIR/DICOM |
| 19 | Waktu/discovery | `Ntp`, `Mdns`, `Ssdp` |

## Lampiran B — Contoh kerangka crate Rust protokol

```rust
// rust/crates/iotcom-modbus/src/lib.rs
pub struct MasterMachine { /* state: pending requests, transaction id, timers */ }

pub enum MasterCommand {
    ReadHolding { unit: u8, addr: u16, count: u16 },
    WriteSingle { unit: u8, addr: u16, value: u16 },
}

pub enum MasterEvent {
    Response { id: u32, data: Vec<u16> },
    Exception { id: u32, code: u8 },
    Timeout { id: u32 },
}

impl Machine for MasterMachine {
    type Event = MasterEvent;
    type Command = MasterCommand;
    // handle_input: parse ADU (TCP MBAP / RTU CRC16) → events
    // handle_command: encode PDU → queue transmit
    // handle_timeout: expire pending requests
}
```

```csharp
// C#: driver loop generik untuk semua Machine
internal sealed class MachineDriver<TMachine> where TMachine : INativeMachine
{
    // PipeReader -> machine.HandleInput -> drain events -> Channel<TEvent>
    // PipeWriter <- machine.PollTransmit
    // Timer <- machine.PollTimeout / HandleTimeout
}
```
