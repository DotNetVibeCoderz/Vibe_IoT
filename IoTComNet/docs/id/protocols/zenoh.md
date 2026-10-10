---
title: Zenoh
translation-status: synced
---

# Zenoh

**Ringkasan.** Eclipse Zenoh menyatukan publish/subscribe, kueri bergaya penyimpanan, dan komputasi antar perangkat,
gateway, dan cloud. Data berada di bawah key expression hierarkis seperti `plant/line1/temp`; subscriber meminta
`plant/*/temp` atau `plant/**`, queryable menjawab permintaan `get`, dan peer saling menemukan lewat multicast scouting
atau lewat router. `IoTCom.Net.Adapters.Zenoh` adalah adapter di atas crate Rust `zenoh` (bukan tulis ulang), dimuat lewat
pustaka native `iotcom_zenoh`; ia menyediakan:

- `ZenohSession`:
  - `PutAsync` dan `DeleteAsync` pada key konkret (teks atau byte, dengan encoding opsional)
  - `Subscribe` (handler yang Anda dispose), event `SampleReceived`, dan `WatchAsync` (`IAsyncEnumerable<ZenohSample>`)
    untuk key expression dengan `*` dan `**`
  - `DeclareQueryable` (`ZenohQuery` dengan key, parameter, dan payload, `ReplyAsync`, `ReplyErrorAsync`) dan `GetAsync`
    (mengumpulkan jawaban dari setiap queryable yang cocok, dengan timeout)
  - abstraksi bersama `IPublisher<T>` / `ISubscriber<T>` untuk `byte[]` dan `string`; delete dilewati di sana
  - mode peer atau client, endpoint `Connect`/`Listen` (`tcp/host:port`, `udp/host:port`), multicast scouting hidup atau
    mati, opsi `ReadOnly`, perubahan status, dan traffic tap
- `ZenohKeyExpr`: validasi dan pencocokan C# murni (`IsValid`, `Includes`, `Intersects`, `IsWild`) mengikuti aturan Zenoh
  untuk `*`, `**`, dan chunk kosong.
- `VirtualZenohNetwork`: jaringan dalam proses untuk pengujian, notebook, dan `--sim`; tidak butuh pustaka native.
- `NativeZenohBackend`: mesin bawaan, zenoh 1.10.1 yang dibangun hanya dengan transport TCP dan UDP.

## Kapan dipakai

- Memindahkan data antar robot, gateway edge, dan server ketika broker merepotkan dan peer harus saling menemukan.
- Menanyakan status perangkat saat ini (`get`) alih-alih menunggu ia mempublikasikannya.
- Memantau seluruh subpohon pabrik dengan satu subscription wildcard.
- Menjalankan kode yang sama terhadap `VirtualZenohNetwork` di pengujian dan terhadap peer sungguhan di lapangan.

## Instalasi

```bash
dotnet add package IoTCom.Net.Adapters.Zenoh --prerelease    # bukan bagian dari meta-package IoTCom.Net
```

Paket ini membawa pustaka native `iotcom_zenoh` untuk platform yang didukung. Jika hanya memakai `VirtualZenohNetwork`,
pustaka native tidak diperlukan.

## Mulai cepat

```csharp
using IoTCom.Net.Adapters.Zenoh;

await using var zenoh = ZenohSession.Create(o => o.Connect("tcp/192.168.1.10:7447"));   // router atau peer lain
await zenoh.ConnectAsync();
await zenoh.PutAsync("plant/line1/temp", "21.5");

await foreach (var sample in zenoh.WatchAsync("plant/**", ct))
    Console.WriteLine($"{sample.Key} = {sample.Text}");
```

Menanyakan status, dan menjawab:

```csharp
using var info = zenoh.DeclareQueryable("plant/line1/info", async q =>
    await q.ReplyAsync("plant/line1/info", "{\"state\":\"running\"}"));

foreach (var reply in await zenoh.GetAsync("plant/*/info"))
    Console.WriteLine(reply.IsError ? reply.ErrorText : reply.Sample!.Text);
```

Tanpa jaringan sama sekali:

```csharp
var net = new VirtualZenohNetwork();
await using var a = ZenohSession.Create(o => o.UseVirtual(net));
await using var b = ZenohSession.Create(o => o.UseVirtual(net));
```

## Keamanan

Mempublikasikan data mengubah apa yang dilihat setiap subscriber yang cocok, yang di jaringan pabrik bisa berupa
setpoint. `PutAsync`, `DeleteAsync`, dan jawaban kueri hidup secara bawaan karena ini adalah adapter pub/sub; setel
`ReadOnly = true` dan semuanya melempar `ReadOnlyModeException`, sementara subscribe dan `get` tetap bekerja. CLI
bersifat hanya-baca untuk `sub` dan `get`, dan mewajibkan `--allow-write` serta konfirmasi untuk `pub`. Multicast scouting
Zenoh mempertemukan setiap sesi di segmen jaringan yang sama; matikan (`MulticastScouting = false`) dan sebutkan endpoint
secara eksplisit bila Anda ingin topologi tertutup. Build ini tidak punya transport TLS atau autentikasi.

## Alat

```bash
iotcom zenoh sub "plant/**" --sim -n 6                                  # pabrik simulasi di jaringan dalam proses
iotcom zenoh sub "plant/**" --connect tcp/192.168.1.10:7447 --no-scouting
iotcom zenoh get "plant/*/info" --sim
iotcom zenoh pub plant/line1/setpoint 42 --sim --allow-write
```

`--mode peer|client`, `--connect`, `--listen`, dan `--no-scouting` membuka sesi sungguhan; `--sim` menjalankan jaringan
dalam proses dengan pabrik kecil yang mempublikasikan `plant/<line>/temp` dan `plant/<line>/pressure` serta menjawab
`plant/*/info`.

## Pengujian

Pengujian key expression mencakup:
- `Includes` dan `Intersects` untuk `*`, `**`, `$*`, dan wildcard campuran, termasuk simetri antar kedua sisi;
- validasi key yang cacat, dan deteksi wildcard.

Pengujian sesi di jaringan virtual mencakup:
- put yang hanya sampai ke subscriber yang cocok di sesi lain, dengan encoding yang benar, serta sesi yang juga menerima
  publikasinya sendiri dan event `SampleReceived`;
- delete yang dikirim sebagai sampel delete, dan tidak ada lagi sampel setelah subscription di-dispose;
- `WatchAsync` yang mengalir sampai dibatalkan, dan abstraksi publisher/subscriber bersama yang melewati delete;
- `GetAsync` yang mengumpulkan jawaban dari setiap queryable yang cocok beserta parameter dan body, jawaban error, tanpa
  queryable, queryable yang lupa menjawab atau melempar, key jawaban yang harus beririsan dengan kueri, dan queryable yang
  sudah tidak dideklarasikan;
- `ReadOnly` yang menolak put, delete, dan reply tetapi mengizinkan subscribe dan get;
- key tidak valid, sesi yang sudah ditutup, perubahan status, dan traffic tap.

Tiga pengujian native berjalan bila pustaka `iotcom_zenoh` sudah dibangun (`cargo build --release -p
iotcom-zenoh-native`) dan dilewati bila tidak: dua sesi native bertukar put, delete, dan kueri lewat TCP (serta `get` kosong
bila tak ada yang menjawab), nilai bawaan yang terdokumentasi beserta validasi mode, dan endpoint yang salah yang
dilaporkan sebagai galat transport.

## Keterbatasan

Build native hanya punya transport TCP dan UDP: tanpa TLS, QUIC, WebSocket, serial, atau shared memory, dan tanpa kontrol
akses. Storage, liveliness, dan attachment tidak diekspos, dan tidak ada publisher yang dideklarasikan (setiap put dikirim
langsung). Ia adalah klien jaringan Zenoh, bukan router: jalankan `zenohd` bila Anda membutuhkannya. Bukan bagian dari
meta-package.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/22-zenoh.id.ipynb` · [Broker pesan](messaging-brokers.md) · [Endpoint dan transport](../concepts/endpoints-and-transports.md)
