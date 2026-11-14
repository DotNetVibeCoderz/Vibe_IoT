---
title: OCPP
translation-status: synced
---

# OCPP 1.6J dan 2.0.1

**Ringkasan.** Open Charge Point Protocol (OCPP) menghubungkan pengisi daya EV dengan back office-nya, CSMS (charging
station management system; "central system" di 1.6). Pengisi membuka WebSocket ke CSMS, dengan identitasnya sebagai
segmen path terakhir dan versi OCPP sebagai subprotokol (`ocpp1.6` atau `ocpp2.0.1`). Kedua sisi lalu bertukar
array JSON kecil: CALL `[2, id, aksi, payload]` dijawab CALLRESULT `[3, id, payload]` atau CALLERROR
`[4, id, kode, deskripsi, detail]`. `IoTCom.Net.Protocols.Ocpp` menyediakan:

- `OcppFrame`, codec RPC sans-I/O: parsing beserta kode galat yang wajib dijawab penerima (1.6 dan 2.0.1 memakai
  nama berbeda), pemformatan, dan frame lane (`DescribeFields`, offset byte UTF-8).
- `OcppSession`, kerangka RPC di atas WebSocket: pemasangan CALL/CALLRESULT dengan satu CALL tertunda per sisi,
  timeout, handler bertipe (`On<TRequest, TResponse>`, `CallAsync`) dan JSON mentah, jawaban otomatis NotImplemented
  dan galat format, serta permintaan yang ditangani di luar loop penerimaan agar handler boleh memanggil balik.
- Pesan bertipe di `V16` dan `V201` (profil core, smart charging, dan remote trigger) dengan JSON hasil source
  generator, sehingga paket dapat di-trim dan kompatibel NativeAOT. `OcppCatalog` mencari tipe sebuah aksi dan
  memvalidasi payload terhadapnya.
- `OcppChargePoint`, sisi pengisi: `ws://` dan `wss://` lewat `ClientWebSocket` (autentikasi Basic untuk security
  profile 1 dan 2, sertifikat klien untuk profile 3), atau transport IoTCom apa pun (`UseInMemory`, `UseTcp`).
- `OcppCsms`, CSMS kecil: upgrade WebSocket HTTP/1.1 miliknya sendiri lewat `UseTcp` / `ListenInMemory` (TLS dan
  pemeriksaan kata sandi opsional), atau `AcceptWebSocketAsync` untuk socket yang diterima ASP.NET Core. CSMS menjawab
  boot, heartbeat, status, otorisasi (daftar id tag yang diizinkan), transaksi, dan meter value, menyimpan model
  `OcppStation` yang hidup, dan mengirim perintah yang tidak bergantung versi: `RemoteStartAsync`, `RemoteStopAsync`,
  `ResetAsync`, `UnlockAsync`, `SetAvailabilityAsync`, `SetPowerLimitAsync` (charging profile),
  `ReadConfigurationAsync`, `WriteConfigurationAsync`, dan `TriggerAsync`.
- `OcppChargePointSimulator`: pengisi lengkap untuk kedua versi dengan konektor, model EV (state of charge, daya
  menurun di atas 80 %, batas daya dari CSMS), meter value, heartbeat, sambung ulang, dan jawaban untuk semua perintah
  di atas.

Sesuai desain, OCPP ditulis dalam C# terkelola (JSON di atas WebSocket, tanpa kembaran Rust). Codec RPC dan 16
payload yang valid menurut skema diperiksa terhadap `/conformance/ocpp.json`, yang dihasilkan referensi Python
independen.

## Kapan dipakai

- Membangun atau menguji CSMS: jalankan puluhan pengisi simulasi dari kedua versi terhadapnya.
- Membangun firmware pengisi atau gateway di .NET (Linux pada pengendali tertanam).
- CSMS privat kecil untuk depo, gedung, atau lab, atau jembatan dari pengisi ke sistem manajemen gedung.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Ocpp --prerelease    # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

Pengisi terhadap CSMS yang sudah ada:

```csharp
using IoTCom.Net.Protocols.Ocpp;
using V16 = IoTCom.Net.Protocols.Ocpp.V16;

await using var cp = OcppChargePoint.Create(o =>
{
    o.UseCsms("wss://csms.example.com/ocpp/");   // identitas ditambahkan: …/ocpp/CP-001
    o.Identity = "CP-001";
    o.Version = OcppVersion.Ocpp16;
    o.Password = Environment.GetEnvironmentVariable("OCPP_PASSWORD");
});
cp.On<V16.RemoteStartTransactionRequest, V16.RemoteStartTransactionResponse>((r, ct) =>
    ValueTask.FromResult(new V16.RemoteStartTransactionResponse { Status = OcppGenericStatus.Accepted }));
await cp.ConnectAsync();
var boot = await cp.CallAsync(new V16.BootNotificationRequest { ChargePointVendor = "Acme", ChargePointModel = "W22" });
```

CSMS dengan pengisi simulasi:

```csharp
await using var csms = OcppCsms.Create(o => o.UseTcp(IPAddress.Any, 8887));   // ws://host:8887/ocpp/{identitas}
csms.TransactionStopped += (station, tx) => Console.WriteLine($"{station.Identity}: {tx}");
await csms.StartAsync();

await using var charger = OcppChargePointSimulator.Create(o => { o.UseCsms("ws://127.0.0.1:8887/ocpp/"); o.Version = OcppVersion.Ocpp201; });
await charger.StartAsync();
await charger.PlugInAsync(1, socPercent: 20);
await charger.PresentCardAsync(1, "04A1B2C3");
await charger.StepAsync(TimeSpan.FromMinutes(30));   // atau AutoRun = true
```

Di dalam ASP.NET Core, terima socket sendiri lalu serahkan:

```csharp
app.Map("/ocpp/{id}", async (HttpContext http, string id) =>
{
    var socket = await http.WebSockets.AcceptWebSocketAsync("ocpp2.0.1");
    await csms.AcceptWebSocketAsync(socket, id, OcppVersion.Ocpp201, http.Connection.RemoteIpAddress?.ToString() ?? "");
});
```

## Keamanan

Mulai dan henti jarak jauh, reset, buka kunci, ketersediaan, konfigurasi, dan batas pengisian mengubah apa yang
dilakukan pengisi dan mobil yang tersambung. `OcppCsms` menolaknya sampai `AllowCommands()`; pembacaan (konfigurasi,
trigger) tetap diizinkan. Gallery dan CLI memasang kunci tulis di depannya (`--allow-write`). Kata sandi tidak pernah
dicatat di log. Pakai `wss://` dengan autentikasi Basic atau sertifikat klien di luar lab, dan biarkan
`AcceptAnyServerCertificate` mati.

## Alat

```bash
iotcom ocpp demo                                          # CSMS dengan pengisi AC (1.6J) dan DC (2.0.1) dalam proses
iotcom ocpp demo --allow-write --frames
iotcom ocpp csms --tags 04A1B2C3,APP-7731 --frames        # CSMS hanya-baca di ws://0.0.0.0:8887/ocpp/{identitas}
iotcom ocpp charger --csms ws://127.0.0.1:8887/ocpp/ --tag 04A1B2C3
iotcom ocpp decode '[2,"1","StartTransaction",{"connectorId":1}]'   # frame lane + pemeriksaan skema
```

Ekstensi VS Code mengurai pesan OCPP (`ocpp`). Gallery: *Hub pengisian EV lewat OCPP*.

## Pengujian

Uji frame mencakup pemformatan CALL/CALLRESULT/CALLERROR, nama field OCPP dalam JSON (`idToken`, `Sample.Periodic`),
field wajib yang hilang, dan offset byte pada frame lane. Ke-35 vektor bersama mencakup contoh dari spesifikasi, 13
pesan rusak beserta kode galat yang dijawab setiap versi, dan 16 payload bertipe yang harus diserialisasi ulang tanpa
perubahan.

Uji sesi menjalankan CSMS dan simulator dari kedua versi dalam proses: boot dan status; kartu yang memulai transaksi
dan pencabutan yang mengakhirinya dengan energi yang benar; kartu tak dikenal ditolak; perintah ditolak dalam mode
hanya-baca, lalu mulai jarak jauh, batas daya yang dipatuhi mobil, dan henti jarak jauh; pembacaan konfigurasi dalam
mode hanya-baca dan penulisan sesudahnya; galat NotImplemented dan format; reset yang me-reboot pengisi; serta link
terputus yang tersambung ulang. Uji TCP menjalankan `ClientWebSocket` terhadap upgrade milik CSMS dengan autentikasi
Basic, dan kata sandi yang salah ditolak.

## Keterbatasan

Pesan bertipe mencakup profil core, smart charging, dan remote trigger; manajemen firmware, daftar otorisasi lokal,
reservasi, diagnostik/log, display message, sertifikat ISO 15118, dan laporan device model 2.0.1
(`GetBaseReport`/`NotifyReport`) lewat API JSON mentah (`CallAsync(aksi, payload)`). CSMS menerapkan satu batas
TxDefaultProfile per konektor; CSMS tidak menyusun tumpukan profil atau jadwal seiring waktu. Antrean transaksi
offline di simulator belum diimplementasikan. OCPP 2.1 belum didukung.

## Pelajari lebih lanjut

Notebook `notebooks/automotive/24-ocpp.id.ipynb` · sampel `samples/console/OcppCharger`
