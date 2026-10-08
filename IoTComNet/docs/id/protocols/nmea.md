---
title: NMEA 0183
translation-status: synced
---

# NMEA 0183

**Ringkasan.** Protokol kalimat ASCII dari penerima GPS/GNSS, transponder AIS, echo sounder, dan instrumen kapal:
`$GPGGA,123519,4807.038,N,...*47`. IoTCom.Net mengurai, memvalidasi, membangun, dan melayaninya, serta menggabungkan
kalimat menjadi satu fix GNSS.

## Kapan dipakai

- Membaca posisi, kecepatan, waktu, dan data satelit dari modul GPS (serial atau USB).
- Mengonsumsi NMEA-over-TCP/UDP dari chart plotter dan gateway kapal.
- Mengemulasi GPS untuk menguji perangkat lunak navigasi.

## Peran

| Peran | Tipe |
|---|---|
| Pembaca / subscriber | `NmeaReader` — pesan bertipe, `IAsyncEnumerable`, `GnssState` |
| Server / publisher | `NmeaServer` — menyiarkan ke banyak client TCP atau port serial |
| Simulator | `NmeaSimulator` — GGA, RMC, VTG, GSA, GSV pada lintasan melingkar |
| Codec | `NmeaSentence`, `NmeaParser` |

Kalimat bertipe: GGA, RMC, GSA, GSV, VTG, GLL, ZDA (lainnya datang sebagai `UnknownNmeaMessage` dengan field mentah).

## Transport

`UseSerial("COM4", 9600)` (kebanyakan penerima: 9600 atau 4800 baud, 8N1), `UseTcp(host, 10110)`, `UseInMemory(listener)`.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Nmea --prerelease
```

## Mulai cepat

```csharp
await using var gps = NmeaReader.Create(o => o.UseSerial("COM4", 9600));
await gps.ConnectAsync();
await foreach (var rmc in gps.ReadAsync<RmcMessage>())
{
    var fix = gps.Gnss.Current;
    Console.WriteLine($"{fix.Latitude:F6}, {fix.Longitude:F6} · {fix.SpeedKmh:F1} km/h · {fix.SatellitesUsed} satelit");
}
```

## Konfigurasi

| Opsi | Default | Deskripsi |
|---|---|---|
| `AcceptInvalidChecksums()` | mati | menyimpan kalimat dengan checksum salah (ditandai lewat `ChecksumValid`) |
| `WithLogger(...)` | — | logging |

## Contoh

```csharp
// Mengurai dan membangun tanpa transport
var gga = (GgaMessage)NmeaParser.Parse("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47")!;
string sentence = NmeaSentence.Build("GP", "GLL", "4916.45", "N", "12311.12", "W", "225444", "A");

// Replay log: alirkan baris rekaman melalui pipeline yang sama
foreach (var line in File.ReadLines("track.nmea")) gps.Process(line);

// Emulator GPS yang bisa disambungi aplikasi lain (tcp://0.0.0.0:10110)
await using var server = NmeaServer.Create(o => o.UseTcp(IPAddress.Any, 10110));
await server.StartAsync();
await new NmeaSimulator(latitude: -6.9147, longitude: 107.6098).RunAsync(server);
```

## Simulator

`NmeaSimulator(latitude, longitude, radiusMeters, speedKmh, seed)` menggerakkan kendaraan pada lingkaran dan
menghasilkan satu epoch penuh per tick. `GenerateEpoch(utc, elapsed)` deterministik untuk pengujian. CLI:
`iotcom nmea simulate --port 10110`.

## Pengujian dan interoperabilitas

Pengujian memakai kalimat kanonik dari contoh referensi NMEA, round-trip koordinat, penolakan checksum, dan alur
end-to-end simulator → server → pembaca.

## Keamanan

NMEA tidak punya autentikasi; siapa pun di link dapat menyisipkan posisi. Perlakukan posisi sebagai input tak
tepercaya di sistem yang terkait keselamatan dan bandingkan dengan sensor lain.

## AIS

Transponder dan penerima AIS mengirim kalimat `!AIVDM` (kapal lain) dan `!AIVDO` (kapal sendiri). Pesan biner
di-armour enam bit per karakter, dan pesan panjang dipecah ke beberapa kalimat. `AisDecoder` menyambung fragmen,
membuka armour bit, dan mengurai tipe 1–3 dan 27 (posisi), 4 (stasiun pangkalan), 5 (data statis dan pelayaran),
18/19 (kelas B), 21 (sarana bantu navigasi), dan 24 (data statis kelas B). `AisTracker` menyimpan tabel kapal, dan
`AisSimulator` menghasilkan lalu lintas di Teluk Jakarta.

```csharp
var ais = new AisDecoder();
var tracker = new AisTracker();
await foreach (var line in reader.ReadLinesAsync())          // atau sumber baris NMEA apa pun (UDP, TCP, serial)
    if (ais.Feed(line) is { } message) tracker.Apply(message);
foreach (var v in tracker.Vessels) Console.WriteLine(v);       // 525005123 KM SINAR JAKARTA -5.9831,106.8602 11.4 kn
```

`AisBits.EncodePosition` dan `AisBits.EncodeStatic` membangun kalimat untuk pengujian dan simulator. CLI mengurai
kalimat (`iotcom nmea ais decode`) dan menampilkan tabel kapal langsung dari simulator, port UDP, atau feed TCP
(`iotcom nmea ais watch --udp 10110`). Pengujian mematok contoh yang diterbitkan (laporan tipe 1 dan tipe 5 dua
kalimat milik *EVER DIADEM*), diperiksa silang dengan decoder independen.

![Lalu lintas pelabuhan](../../images/gallery-ais.png)

## Keterbatasan

Pesan aplikasi biner AIS (tipe 6, 8, 25, 26) dilaporkan sebagai tipe tak dikenal, dan AIS diurai tetapi tidak
di-encode untuk semua tipe. Tahun dua digit pada RMC memakai pivot 80 (80–99 → 19xx, 00–79 → 20xx).

## Pelajari lebih lanjut

Notebook `notebooks/navigation/03-nmea.id.ipynb` · demo Galeri *Pelacak kendaraan GNSS* dan *Lalu lintas pelabuhan (AIS)* · `iotcom nmea --help`
