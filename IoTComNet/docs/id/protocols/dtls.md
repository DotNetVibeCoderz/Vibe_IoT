---
title: DTLS
translation-status: synced
---

# DTLS 1.2 (coaps://)

**Ringkasan.** DTLS adalah TLS untuk datagram: handshake dan perlindungan record yang sama, ditambah timer
retransmisi, nomor urut, dan pertukaran cookie agar bekerja di atas UDP. CoAP (RFC 7252 §9) dan LwM2M mengamankan
lalu lintasnya dengan DTLS di UDP 5684 (`coaps://`). `IoTCom.Net.Transport.Dtls` adalah adapter di atas BouncyCastle
(MIT), stack DTLS terkelola yang paling banyak dipakai, dan menyediakan:

- `DtlsClientTransport` dan `DtlsServerTransport`: `IDatagramTransport` yang mengenkripsi semua yang dikirim dan
  mendekripsi semua yang diterima, sehingga protokol datagram apa pun (CoAP, LwM2M, protokol Anda sendiri) berjalan
  tanpa perubahan.
- Mode PSK: `TLS_PSK_WITH_AES_128_CCM_8` (suite wajib untuk perangkat terbatas, RFC 7925) dan
  `TLS_PSK_WITH_AES_128_GCM_SHA256`, dengan pencarian identitas di sisi server (`PskLookup`, `UsePsk`).
- Mode sertifikat: `TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8` dan `..._AES_128_GCM_SHA256`; klien menerima server lewat
  sidik jari SHA-256 yang di-pin atau lewat rantai ke root yang Anda berikan (tanpa trust store OS; jaringan pabrik
  jarang punya CA publik). `DtlsCertificates.CreateSelfSigned` membuat sertifikat P-256 untuk lab dan deployment ber-pin.
- Server: cookie HelloVerifyRequest sebelum pekerjaan apa pun (tanpa amplifikasi), satu sesi per alamat peer,
  kedaluwarsa saat idle, `Sessions`, serta event `SessionEstablished` (cipher suite, identitas PSK) dan
  `HandshakeFailed`.
- Ekstensi builder `UseDtlsClient` / `UseDtlsServer` untuk setiap endpoint datagram.

Sesuai desain (tingkat kurasi 2), DTLS dibungkus, tidak dibangun ulang: BouncyCastle membawa kriptografi dan mesin
status handshake, sementara IoTCom menambahkan model transport, demultipleksing, dan diagnostik.

## Kapan dipakai

- Perangkat CoAP atau server LwM2M yang mewajibkan `coaps://` (Leshan, Eclipse Wakaama, Californium, perangkat nRF/Zephyr).
- Mengamankan protokol UDP Anda sendiri tanpa merancang pertukaran kunci.
- Menguji profil DTLS perangkat di dalam proses (identitas PSK, suite, penanganan kunci salah, link yang lossy).

## Instalasi

```bash
dotnet add package IoTCom.Net.Transport.Dtls --prerelease    # paket terpisah: tidak termasuk meta-package
```

## Mulai cepat

CoAP lewat DTLS dengan pre-shared key:

```csharp
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Transport.Dtls;

var server = new IPEndPoint(IPAddress.Parse("192.168.1.40"), 5684);
var psk = DtlsPsk.FromHex("greenhouse-node-01", Environment.GetEnvironmentVariable("NODE_PSK")!);
await using var coap = CoapClient.Create(o => o
    .UseDtlsClient(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0)), server, new DtlsClientOptions { Psk = psk })
    .UseServer(server));
await coap.ConnectAsync();
Console.WriteLine((await coap.GetAsync("/sensors/temperature")).PayloadText);
```

Server dengan PSK sekaligus sertifikat:

```csharp
var options = new DtlsServerOptions { Certificate = DtlsCertificates.CreateSelfSigned("gw.plant.local") }
    .UsePsk(new Dictionary<string, byte[]> { ["node-01"] = key1, ["node-02"] = key2 });
await using var server = CoapServer.Create(o => o.UseDtlsServer(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 5684)), options));
```

Klien yang mem-pin sertifikat server:

```csharp
var pinned = new DtlsClientOptions();
pinned.PinnedFingerprints.Add("353D674FFD7C5F4DA1BFB19E98A4C0ADD6E521835EC10FBB6A6442BC853D2604");
var dtls = new DtlsClientTransport(new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0)), server, pinned);
await dtls.HandshakeAsync();            // melempar dengan alasannya: sertifikat tak tepercaya, PSK ditolak, timeout
Console.WriteLine(dtls.CipherSuite);    // EcdheEcdsaAes128Ccm8
```

LwM2M memakai ekstensi yang sama (opsi `Lwm2mClient`/`Lwm2mServer` adalah builder datagram).

## Keselamatan

Kunci adalah rahasia. `DtlsPsk.ToString()` tidak pernah menampilkan kunci, IoTCom tidak pernah mencatatnya, dan CLI
membacanya dari `IOTCOM_DTLS_PSK` agar tidak tersimpan di riwayat shell. Mode sertifikat tanpa pin atau root menolak
setiap server — tidak ada sakelar "terima apa saja". Record dengan MAC salah dibuang diam-diam sesuai RFC 6347,
sehingga kunci yang salah terlihat sebagai timeout handshake, bukan alert; pesan galatnya menyebutkan apa yang perlu
diperiksa.

## Alat

```bash
export IOTCOM_DTLS_PSK=node-01:000102030405060708090A0B0C0D0E0F
iotcom coap serve --psk node-01:000102030405060708090A0B0C0D0E0F     # node rumah kaca di coaps://0.0.0.0:5684
iotcom coap get coaps://127.0.0.1/sensors/temperature                 # PSK dari IOTCOM_DTLS_PSK
iotcom coap serve --cert                                              # sertifikat self-signed, mencetak sidik jarinya
iotcom coap discover coaps://127.0.0.1/ --pin 353D674F…2604
```

Setiap perintah `coap` menerima `coaps://` dengan `--psk` atau `--pin`. Capture dan `--frames` menampilkan pesan CoAP
yang sudah didekripsi; record terenkripsi tetap di kabel.

## Pengujian

Tes menjalankan CoAP lewat DTLS di memori: sesi PSK (GET, PUT), suite yang dinegosiasikan dan identitas PSK yang
dilihat server, kunci salah dan identitas tak dikenal ditolak, mode sertifikat dengan sidik jari ber-pin dan dengan
root tepercaya, pin salah ditolak (dan kegagalannya dilaporkan lagi pada panggilan berikutnya), handshake di bawah
kehilangan paket 20 %, serta klien LwM2M yang mendaftar lewat DTLS. Sampel `CoapsSecure` menjalankan hal yang sama
lewat UDP sungguhan.

## Batasan

DTLS 1.3 (RFC 9147) belum ditawarkan: stack terkelola BouncyCastle belum menuntaskannya, dan alternatifnya adalah build
OpenSSL native; direncanakan setelah implementasi terkelola tersedia. Connection ID (RFC 9146), sertifikat klien
(mutual TLS), raw public key (RFC 7250), resumption sesi, dan OSCORE belum diimplementasikan.

## Pelajari lebih lanjut

Notebook `notebooks/network/28-dtls.id.ipynb` · sampel `samples/console/CoapsSecure` · [CoAP](coap.md) ·
[LwM2M](lwm2m.md)
