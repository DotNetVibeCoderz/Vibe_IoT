---
title: NTP / SNTP
translation-status: synced
---

# NTP / SNTP

**Ringkasan.** Perangkat butuh waktu yang benar untuk memberi cap waktu pada pengukuran, mengurutkan kejadian, merotasi
log, dan memeriksa sertifikat. Kristalnya melenceng puluhan bagian per juta, yang terkumpul menjadi beberapa detik
sehari. NTP (RFC 5905) mengukur jam terhadap server dengan empat cap waktu di UDP port 123. SNTP (RFC 4330) adalah
bentuk klien sederhananya: satu permintaan, beberapa pemeriksaan, lalu aplikasi menerapkan offset.
`IoTCom.Net.Protocols.Ntp` menyediakan:

- `SntpClient`:
  - bertanya ke satu atau beberapa server (`SynchronizeAsync` membuang jawaban yang lambat dan mengambil median
    offset, sehingga satu server yang salah kalah suara)
  - menerapkan pemeriksaan RFC 4330: jawaban harus menggemakan waktu kirim kita (yang bit rendahnya acak), datang dari
    server dalam mode server, membawa waktu kirim yang tidak nol, dan tidak menyalakan alarm leap
  - penanganan kiss-o'-death (`NtpKissOfDeathException` dengan RATE, DENY…)
  - interval polling minimum 15 dtk per server secara bawaan, seperti yang diharapkan pool publik
  - `Clock` yang bisa diganti, sehingga ia dapat mendisiplinkan jam perangkat atau jam simulasi; ia tidak pernah
    mengubah jam sistem
- `NtpServer`: menjawab permintaan klien dari jam apa pun, dengan stratum, reference identifier, dan leap indicator,
  pembatasan laju opsional dengan kiss-o'-death RATE, serta jeda pemrosesan simulasi.
- `NtpPacket`, `NtpTimestamp` (sadar era: 1968–2104 melewati pergantian 2036), `NtpMath` (offset dan delay), dan
  frame lane (`NtpPacket.Describe`).
- `DriftingClock`: jam dengan offset dan lenceng dalam ppm, untuk simulasi, pengujian, dan Gallery.

Codec-nya punya kembaran Rust yang di-fuzz (`rust/crates/iotcom-ntp`) dan diperiksa dengan vektor
`/conformance/ntp.json` yang sama, yang berasal dari referensi Python independen.

## Kapan dipakai

- Memeriksa seberapa jauh jam gateway, PLC, atau perangkat lapangan meleset sebelum memercayai cap waktunya.
- Menjaga jam tingkat aplikasi (data logger, simulator, RTC perangkat tertanam) tetap selaras dengan server.
- Menyajikan waktu di jaringan pabrik atau kapal yang terisolasi dari host yang didisiplinkan GPS.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Ntp --prerelease    # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Ntp;

await using var sntp = SntpClient.Create(o => o.UseServer("pool.ntp.org").UseServer("time.cloudflare.com"));
var estimate = await sntp.SynchronizeAsync();
foreach (var r in estimate.Accepted) Console.WriteLine(r);   // offset, delay, stratum, referensi
Console.WriteLine($"jam lokal meleset {estimate.Offset.TotalMilliseconds:+0.0} ms");
```

Mendisiplinkan jam perangkat:

```csharp
var rtc = new DriftingClock(TimeSpan.Zero, driftPpm: 0);   // atau bungkus jam Anda sendiri di Clock
await using var sntp = SntpClient.Create(o => { o.UseServer("10.0.0.1"); o.Clock = () => rtc.UtcNow; });
var r = await sntp.QueryAsync();
rtc.Step(r.Offset);
```

Server di jaringan lokal:

```csharp
await using var server = NtpServer.Create(o => o.UseUdp(123).WithReference("GPS", stratum: 1));
await server.StartAsync();
```

## Menjadi warga yang baik

Server publik dijalankan oleh relawan. Tanyai paling sering setiap beberapa menit per perangkat dan pertahankan
`MinimumPollInterval` bawaan (15 dtk). Saat server mengirim kiss-o'-death RATE, mundurlah; pada DENY atau RSTR berhenti
memakainya. Menyajikan waktu di port 123 memerlukan hak administrator di kebanyakan sistem, dan server yang Anda
jalankan sebaiknya mengiklankan `NtpLeap.Unsynchronised` selama tidak punya referensi yang baik.

## Alat

```bash
iotcom ntp query                                    # pool.ntp.org
iotcom ntp query time.cloudflare.com pool.ntp.org
iotcom ntp query --sim --frames                     # tiga server dalam proses, salah satunya meleset 30 dtk
iotcom ntp serve --port 1123 --ref LOCL --stratum 10
```

Ekstensi VS Code mengurai paket NTP (`ntp`), dan tangkapan pcapng memakai UDP port 123 sehingga Wireshark
mengurainya. Gallery: *Sinkronisasi jam armada lewat NTP*.

## Pengujian

Uji codec mencakup:
- epoch Unix sebagai `0x83AA7E80` detik, pecahan setengah detik, dan perjalanan pulang-pergi 100 ns;
- batas era 2036, selisih on-wire yang melewatinya, dan tanggal di luar jangkauan;
- paket permintaan dan jawaban dengan root delay dan dispersion, reference identifier ASCII dan IPv4, kiss-o'-death,
  trailer MAC, dan paket yang terlalu pendek;
- aritmetika offset dan delay RFC 5905.

Uji sesi memakai jaringan dalam memori dengan latensi:
- jam yang melenceng diukur dan dikoreksi hingga beberapa milidetik;
- pembatasan laju dengan kiss-o'-death RATE, server yang tidak tersinkron, dan timeout;
- jawaban palsu dengan originate timestamp yang salah diabaikan sebelum jawaban asli diterima;
- mode selain klien tidak dijawab dan versi permintaan digemakan;
- tiga server di mana median mengalahkan satu yang meleset 30 dtk, plus server yang diam dilaporkan gagal.

Ke-25 vektor bersama (paket, cap waktu di kedua era, dan pertukaran) dijalankan di suite uji C# dan Rust. Codec Rust
di-fuzz sebanyak 6,7 juta kali tanpa temuan.

## Keterbatasan

Ini SNTP: tanpa clock filter, tanpa pemilihan peer selain median, tanpa loop disiplin frekuensi, tanpa asosiasi
symmetric atau broadcast, dan tanpa NTS (RFC 8915) maupun autentikasi kunci simetris (MAC dibawa, tidak diperiksa).
Ia tidak mengatur jam sistem operasi; gunakan layanan waktu OS untuk itu.

## Pelajari lebih lanjut

Notebook `notebooks/network/19-ntp.id.ipynb` · sampel `samples/console/NtpClock` ·
[Endpoint dan transport](../concepts/endpoints-and-transports.md)
