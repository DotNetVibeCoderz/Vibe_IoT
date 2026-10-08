---
title: LoRaWAN
translation-status: synced
---

# LoRaWAN

**Ringkasan.** LoRaWAN membawa pesan kecil yang jarang dikirim dari sensor bertenaga baterai, menempuh jarak
berkilo-kilometer di spektrum tanpa lisensi. Perangkat memancarkan frame LoRa, gateway mana pun yang berada dalam
jangkauan meneruskannya ke network server, lalu network server memeriksa, menghapus duplikat, dan mendekripsinya.
IoTCom.Net mengimplementasikan LoRaWAN 1.0.x dari ujung ke ujung:

- **codec PHYPayload** dengan MIC AES-CMAC, enkripsi payload, join OTAA, dan ABP;
- **MAC command** dan **parameter regional** (EU868, US915, AS923-2) beserta waktu di udara;
- **protokol packet-forwarder Semtech UDP**, di sisi gateway maupun server;
- **network server ringan**, **MAC end device** Class A, Cayenne LPP, dan simulator dengan gateway, sensor, serta
  model radio.

![Monitor jaringan LoRaWAN](../../images/gallery-lorawan.png)

## Kapan digunakan

- Menjalankan lab, kampus, atau pilot dengan gateway sendiri, tanpa harus mengoperasikan ChirpStack atau The Things
  Stack terlebih dahulu.
- Menguji perangkat, decoder, dan dashboard terhadap jaringan yang Anda kendalikan, termasuk MIC yang salah, replay,
  dan uplink yang hilang.
- Menggerakkan network server sungguhan (ChirpStack, TTS) dengan gateway dan perangkat simulasi untuk uji beban dan
  integrasi.
- Mengurai frame yang ditangkap dari log gateway atau packet forwarder.

Untuk armada produksi dengan roaming, multicast, dan perangkat Class B/C, gunakan network server lengkap. Codec,
simulator, dan alat di sini tetap bisa dipakai berdampingan dengannya.

## Peran

| Peran | Tipe |
|---|---|
| Network server | `LoRaWanNetworkServer`: perangkat OTAA/ABP, `UplinkReceived`, `DeviceJoined`, `FrameRejected`, `EnqueueDownlink`, `EnqueueMacCommand` |
| Gateway (packet forwarder) | `SemtechPacketForwarder`: PUSH_DATA/PULL_DATA/TX_ACK, laporan status, `TransmitRequested` untuk downlink |
| End device | `LoRaWanEndDevice` (sans-I/O): `CreateJoinRequest`, `CreateUplink`, `HandleDownlink`, jawaban MAC |
| Codec | `LoRaWanPacket`, `LoRaWanJoinAccept`, `LoRaWanCrypto`, `LoRaWanMacCommands`, `SemtechPacket`, `LoRaWanAnatomy`, `CayenneLpp` |
| Simulator | `LoRaWanSimulator`: gateway dan sensor (lingkungan, tanah, meter air, pelacak GPS) dengan path loss dan shadowing |

## Transport

Gateway berbicara dengan server lewat UDP (protokol Semtech, port 1700): `UseUdp(port)`, atau `UseInMemory(network)`
di `InMemoryDatagramNetwork` untuk pengujian. Radio LoRa sendiri milik gateway; simulator memodelkannya.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.LoRaWan --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat: network server untuk gateway Anda

```csharp
using IoTCom.Net.Protocols.LoRaWan;

await using var ns = LoRaWanNetworkServer.Create(o => o.Region = LoRaRegion.AS923Group2);
ns.AddDevice(LoRaWanDeviceRegistration.Otaa(Eui64.Parse("70B3D57ED0000001"), LoRaWanKeys.Parse(appKeyHex), "node-1"));
ns.UplinkReceived += (_, up) =>
    Console.WriteLine($"{up.Device.Name} FCnt {up.FCnt} via {up.Gateways.Count} gateway(s): {Convert.ToHexString(up.Payload)}");
await ns.StartAsync();                    // UDP 1700

ns.EnqueueDownlink(Eui64.Parse("70B3D57ED0000001"), fport: 10, [0x00, 0x3C]);   // dikirim di RX1 setelah uplink berikutnya
```

Arahkan packet forwarder setiap gateway ke mesin ini: di `global_conf.json` (atau `local_conf.json`), isi
`server_address` dengan IP-nya serta `serv_port_up` dan `serv_port_down` dengan 1700.

## Mulai cepat: mengurai frame

```csharp
var frame = LoRaWanPacket.Decode(Convert.FromHexString("40F17DBE4900020001954378762B11FF0D"));
var keys = LoRaWanSessionKeys.FromHex("44024241ED4CE9A68C6A8BC055233FD3", "EC925802AE430CA77FD3DD73CB2CC588");
Console.WriteLine($"{frame}  MIC ok: {frame.VerifyMic(keys.NwkSKey)}");      // Unconfirmed up DevAddr=49BE7DF1 FCnt=2 FPort=1 4 B
Console.WriteLine(Encoding.ASCII.GetString(frame.DecryptPayload(keys)));       // test
```

## Yang dikerjakan network server

| Langkah | Perilaku |
|---|---|
| Gateway | menjawab PUSH_DATA dengan PUSH_ACK dan PULL_DATA dengan PULL_ACK, mengingat alamat downlink setiap gateway, menyimpan laporan statusnya |
| Deduplikasi | salinan satu uplink dari beberapa gateway dikumpulkan selama `DeduplicationWindow` (200 ms); event mencantumkan setiap gateway, SNR terbaik lebih dulu |
| Join | memeriksa DevEUI, JoinEUI, dan MIC, menolak DevNonce yang dipakai ulang, memberi DevAddr (NwkID dari NetID), menurunkan session key, dan menjawab setelah `JoinAcceptDelay` (5 detik) |
| Uplink | menemukan perangkat lewat DevAddr dan MIC, menyusun ulang FCnt 32-bit, menolak replay, mendekripsi FRMPayload, dan mengurai MAC command |
| Downlink | Class A di RX1 (kanal dan data rate yang sama di EU868 dan AS923; dipetakan di US915), `tmst` = uplink + jeda RX1; mengirim ACK untuk uplink confirmed, data dalam antrean (dengan FPending), dan jawaban MAC |
| MAC command | menjawab LinkCheckReq (margin dari SNR, jumlah gateway) dan DeviceTimeReq; mencatat DevStatusAns (baterai, margin) |

Frame yang ditolak memicu `FrameRejected` dengan alasan yang jelas, misalnya: perangkat tidak dikenal, MIC tidak cocok
(AppKey salah), DevNonce sudah dipakai, atau FCnt tidak lebih besar dari sebelumnya (replay).

## Menyimulasikan deployment

```csharp
var options = LoRaWanSimulatorOptions.Demo(new IPEndPoint(IPAddress.Loopback, 1700));   // 2 gateway, 4 sensor
await using var sim = new LoRaWanSimulator(options);
foreach (var r in sim.Registrations) ns.AddDevice(r);
sim.RadioActivity += (_, e) => Console.WriteLine($"{(e.Uplink ? "▲" : "▼")} {e.Device} {e.DataRate} {e.Summary}");
await sim.StartAsync();
```

Setiap gateway adalah `SemtechPacketForwarder` sungguhan, sehingga simulator bekerja dengan network server mana pun
yang kompatibel dengan Semtech. RSSI setiap uplink berasal dari model path loss log-distance (perkotaan, 868–923 MHz)
ditambah shadowing log-normal. Sebuah gateway hanya mendengar uplink jika SNR-nya di atas batas demodulasi untuk
spreading factor tersebut. Perangkat memilih data rate tercepat yang didukung link-nya. Downlink dipancarkan pada
`tmst` yang diminta server.

## Region dan waktu di udara

| Region | Kanal uplink | RX2 | Catatan |
|---|---|---|---|
| `EU868` | 868,1, 868,3, 868,5 MHz | 869,525 MHz, DR0 | duty cycle 1 % di sebagian besar sub-band |
| `US915` | sub-band 2 (903,9–905,3 MHz) | 923,3 MHz, DR8 | RX1 di 923,3 + 0,6 × (kanal mod 8) MHz |
| `AS923Group2` | 921,4, 921,6 MHz | 921,4 MHz, DR2 | AS923-2: Indonesia, Vietnam |

`LoRaAirtime.Compute(bytes, sf, bw)` memakai rumus Semtech. Misalnya, payload 12 byte memakan 61,7 ms di SF7 dan
1,48 detik di SF12; `iotcom lorawan airtime 12` mencetak tabel lengkapnya.

## Alat

```bash
iotcom lorawan server --sim --region AS923                 # network server + gateway dan sensor simulasi
iotcom lorawan server --devices devices.json --pcap lora.pcapng --frames
iotcom lorawan simulate --server chirpstack.local:1700     # menggerakkan network server lain
iotcom lorawan decode 40F17DBE4900020001954378762B11FF0D --nwkskey … --appskey …
iotcom lorawan airtime 12 --region AS923
dotnet run --project samples/console/LoRaWanGatewayMonitor
```

Capture pcapng memakai enkapsulasi LoRaTap, sehingga Wireshark mengurai lapisan LoRaWAN. Ekstensi VS Code mengurai
frame `lorawan` dan `semtech-udp` serta dapat memantau `sim:lorawan` atau `lorawan:udp:<port>`.

## Pengujian dan interoperabilitas

- **Codec:** C# dan crate Rust `iotcom-lorawan` menjalankan vektor yang sama (`/conformance/lorawan.json`). Vektor
  dihasilkan oleh referensi Python yang berdiri sendiri; AES dan AES-CMAC-nya diperiksa terhadap FIPS-197 dan
  RFC 4493, dan keluarannya terhadap frame yang diterbitkan bersama pustaka lora-packet. Codec Rust di-fuzz
  (`cargo fuzz run lorawan`).
- **Semtech UDP:** round trip dan contoh `txpk` dari PROTOCOL.TXT Semtech, termasuk base64 tanpa padding yang dikirim
  packet forwarder sungguhan.
- **Jaringan:** join OTAA dan uplink confirmed lewat dua gateway (satu event, dua penerimaan), downlink dan MAC command
  dalam antrean, penolakan perangkat tak dikenal, AppKey salah, kegagalan MIC, dan replay, perangkat di luar
  jangkauan, serta timing RX1 lewat UDP sungguhan.

## Keamanan

Network server menyimpan root key dan session key, jadi perlakukan host-nya dan file perangkat sebagai rahasia. Jangan
pernah mencatat (log) atau meng-commit key. Protokol Semtech UDP antara gateway dan server tidak diautentikasi dan
tidak dienkripsi: tempatkan di jaringan tepercaya atau VPN. Payload LoRaWAN tetap terenkripsi ujung ke ujung dengan
AppSKey.

## Keterbatasan

Hanya LoRaWAN 1.0.x; LoRaWAN 1.1 (network key terpisah, enkripsi FOpts) diurai secara struktural tetapi tidak
diproses. Belum tersedia: Class B dan C, keputusan ADR di server, rencana kanal CFList per perangkat, multicast,
FUOTA, roaming, dan protokol Basics Station.

## Pelajari lebih lanjut

Demo Galeri *Monitor jaringan LoRaWAN* · notebook `notebooks/lpwan/09-lorawan.id.ipynb` · `iotcom lorawan --help` ·
[Traffic tap dan pcapng](../concepts/traffic-tap.md)
