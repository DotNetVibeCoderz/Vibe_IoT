---
title: Art-Net 4 dan sACN (DMX512 lewat IP)
translation-status: synced
---

# Art-Net 4 dan sACN (DMX512 lewat IP)

**Ringkasan.** DMX512 mengendalikan lampu panggung, arsitektural, dan efek: 512 kanal satu byte per *universe*.
Art-Net dan sACN (ANSI E1.31) membawa universe lewat UDP. IoTCom.Net mengirim dan menerima keduanya, menemukan node
Art-Net, dan memodelkan universe lengkap dengan fade.

## Kapan dipakai

- Mengendalikan fixture LED, dimmer, pixel strip, dan moving light dari .NET.
- Membangun konsol lampu, visualiser, atau jembatan dari sensor/MQTT ke lampu.
- Memantau apa yang dikirim sebuah konsol.

## Peran

| Peran | Tipe |
|---|---|
| Pengirim / penerima / discovery | `ArtNetNode` — ArtDmx, ArtPoll/ArtPollReply, ArtSync |
| Pengirim / penerima | `SacnNode` — multicast per universe, prioritas, aturan sequence E1.31 |
| Model universe | `DmxUniverse` — kanal berbasis 1, snapshot, crossfade |
| Codec | `ArtNetPacket`, `SacnPacket` |

## Transport

Hanya UDP: Art-Net port 6454 (broadcast atau unicast), sACN port 5568 (multicast `239.255.{hi}.{lo}` atau unicast).
`Bind(address, port)` memilih interface lokal; `SendTo(endpoint)` memaksa unicast.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Dmx --prerelease
```

## Mulai cepat

```csharp
await using var node = ArtNetNode.Create(o => o.WithName("Stage left"));
await node.StartAsync();
await node.SendDmxAsync(universe: 0, new byte[] { 255, 120, 0 });   // fixture 1: amber
```

```csharp
await using var sacn = SacnNode.Create(o => o.WithPriority(150));
await sacn.StartAsync();
sacn.JoinUniverse(1);
await foreach (var frame in sacn.ReceiveAsync(universe: 1)) Console.WriteLine(frame.Data.Span[0]);
```

## Konfigurasi

| Opsi | Berlaku untuk | Deskripsi |
|---|---|---|
| `Bind(address, port)` | keduanya | interface/port lokal (port 0 = ephemeral) |
| `SendTo(endpoint)` | keduanya | tujuan unicast |
| `WithName(name)` | keduanya | short name Art-Net / source name sACN |
| `RespondToPoll` | Art-Net | menjawab ArtPoll (default true) |
| `WithPriority(0–200)` | sACN | prioritas sumber (default 100) |
| `Cid` | sACN | component id yang stabil |

## Contoh

```csharp
// Menemukan node
await node.PollAsync();
await Task.Delay(2000);
foreach (var n in node.Nodes) Console.WriteLine($"{n.ShortName} @ {n.Address}");

// Port-address = Net(7) | SubNet(4) | Universe(4)
int universe = ArtNetPacket.PortAddress(net: 0, subNet: 1, universe: 2);

// Fade halus
var u = new DmxUniverse(0);
var from = u.Snapshot();
for (var p = 0.0; p <= 1; p += 0.02) { u.Crossfade(from, target, p); await node.SendDmxAsync(0, u.Snapshot()); await Task.Delay(25); }
```

Konsol mengirim ulang setiap universe terus-menerus (biasanya 25–44 fps) agar penerima pulih dari paket yang hilang —
lakukan hal yang sama, seperti demo Galeri dan sampel `ArtNetPlayer`.

## Pengujian dan interoperabilitas

Round-trip codec (port-address, padding panjang genap, panjang layer sACN untuk 512 slot, aturan sequence E1.31) dan
pertukaran langsung lewat loopback antara dua node, termasuk discovery ArtPoll.

## Keamanan

Kedua protokol tidak mengautentikasi pengirim. Pisahkan jaringan lampu dari jaringan kantor; di jaringan bersama
utamakan sACN dengan unicast dan prioritas per universe.

## Keterbatasan

Art-Net RDM, ArtAddress/ArtIpProg, serta paket universe discovery / sinkronisasi sACN belum diimplementasikan.

## Pelajari lebih lanjut

Demo Galeri *Lampu panggung lewat Art-Net* · sampel `samples/console/ArtNetPlayer` · `iotcom artnet --help`
