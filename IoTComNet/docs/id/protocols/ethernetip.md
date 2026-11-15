---
title: EtherNet/IP
translation-status: synced
---

# EtherNet/IP (explicit messaging)

**Ringkasan.** EtherNet/IP adalah Common Industrial Protocol (CIP) di atas Ethernet, dipakai Rockwell/Allen-Bradley
serta banyak PLC, drive, dan I/O lainnya. Originator (HMI, SCADA, gateway) membuka TCP 44818, mendaftarkan sesi, dan
mengirim permintaan CIP di dalam SendRRData — "explicit messaging". Setiap permintaan menyebut objek dengan EPATH
(class, instance, attribute) atau, pada pengendali Logix, tag simbolik. `IoTCom.Net.Protocols.EtherNetIp`
menyediakan:

- Codec (sans-I/O): `EnipPacket` (header enkapsulasi 24 byte, Common Packet Format, helper SendRRData, frame lane),
  `CipPath` (segmen logis 8/16/32-bit, segmen simbolik berpadding, indeks elemen), `CipRequest`, `CipResponse`
  (general dan additional status), `EnipIdentity` (item ListIdentity dan atribut Identity), serta `CipTagValue` (tipe
  elementer Logix BOOL…LREAL, array).
- `EnipClient`: RegisterSession/UnregisterSession, `RequestAsync` untuk layanan CIP apa pun, Get_Attribute_Single,
  Get_Attributes_All, Set_Attribute_Single, `ReadIdentityAsync`, Read Tag dan Write Tag Logix (elemen dan array),
  `ReadTagsAsync` dalam satu Multiple_Service_Packet, ListIdentity, serta routing Unconnected_Send ke slot backplane
  (`UseSlot(0)`). **Hanya-baca secara bawaan.**
- `EnipDiscovery.ListIdentityAsync`: penemuan lewat broadcast UDP.
- `EnipAdapter`: target untuk explicit messaging dengan sesi, ListIdentity lewat TCP dan UDP, ListServices, dan
  message router yang melayani objek Identity, objek Assembly (atribut 3), Multiple_Service_Packet, Unconnected_Send,
  dan tag simbolik dengan pemeriksaan tipe, jangkauan, dan akses.
- `EnipPlcSimulator`: pengendali lini pengemasan (kecepatan, konveyor, penghitung baik/reject, suhu dan set point
  seal, resep, kode gangguan, empat suhu zona, assembly input 100 dan assembly output 150).

Sesuai desain, runtime-nya C# terkelola; crate Rust `iotcom-enip` adalah kembaran codec yang di-fuzz, dan keduanya
menjalankan 28 vektor di `/conformance/enip.json` yang dihasilkan referensi Python independen.

## Kapan dipakai

- Membaca dan menulis tag Logix dari .NET: dasbor, konektor MES dan historian, OEE.
- Berbicara dengan drive, blok I/O, dan device CIP lain lewat objek dan atributnya.
- Target uji untuk HMI dan gateway tanpa PLC (`EnipAdapter` atau `iotcom enip serve`).

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.EtherNetIp --prerelease    # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.EtherNetIp;

await using var plc = EnipClient.Create(o => o.UseTcp("192.168.1.20", EnipPacket.Port).UseSlot(0));   // hanya-baca
await plc.ConnectAsync();
Console.WriteLine(await plc.ReadIdentityAsync());
CipTagValue speed = await plc.ReadTagAsync("Line_Speed");
foreach (var (tag, value, error) in await plc.ReadTagsAsync(["Count_Good", "Count_Reject", "Seal_Temp"]))
    Console.WriteLine($"{tag} = {value?.ToString() ?? error!.Message}");
```

Penulisan, setelah diizinkan:

```csharp
await using var hmi = EnipClient.Create(o => o.UseTcp("192.168.1.20", EnipPacket.Port).UseSlot(0).AllowWrites());
await hmi.ConnectAsync();
await hmi.WriteTagAsync("Seal_Setpoint", CipTagValue.Of(CipDataType.Real, 172));
```

Target milik Anda sendiri:

```csharp
await using var adapter = EnipAdapter.Create(o => { o.UseTcp(IPAddress.Any, 44818); o.UseUdp(44818); });
var level = adapter.AddTag("Tank_Level", CipTagValue.Of(CipDataType.Real, 42.5), writable: false);
await adapter.StartAsync();
level.Set(43.1);
```

## Keamanan

Penulisan tag dan atribut mengubah apa yang dilakukan mesin. Klien menolaknya sampai `AllowWrites()`; CLI membutuhkan
`--allow-write` dan bertanya sebelum menulis ke pengendali sungguhan, dan Gallery punya kunci tulis. Menulis ke
pengendali Logix yang sedang berjalan melewati logika programnya untuk nilai itu — koordinasikan dengan insinyur
kontrol. EtherNet/IP tidak punya autentikasi (CIP Security belum diimplementasikan); tempatkan pengendali di jaringan
terpisah. `EnipAdapter` dapat menolak semua penulisan dengan `WriteProtected`.

## Alat

```bash
iotcom enip demo [--allow-write]                                      # sesi, identitas, tag dalam proses
iotcom enip discover                                                  # broadcast ListIdentity (--broadcast 192.168.1.255)
iotcom enip identity --host 192.168.1.20 --slot 0
iotcom enip read Line_Speed,Count_Good,Seal_Temp --host 192.168.1.20 --slot 0
iotcom enip read Zone_Temps --count 4 --sim
iotcom enip write Line_Speed REAL 90 --host 192.168.1.20 --slot 0 --allow-write
iotcom enip attr 0x01 1 7 --sim                                       # Get_Attribute_Single
iotcom enip serve                                                     # PLC simulasi di TCP/UDP 44818
iotcom enip decode "4C 06 91 0A 4C 69 6E 65 5F 53 70 65 65 64 01 00"
```

Rekaman memetakan EtherNet/IP ke TCP 44818 sehingga Wireshark menguraikan ENIP dan CIP; ekstensi VS Code mengurai
`enip`. Gallery: *Lini pengemasan lewat EtherNet/IP*.

## Pengujian

Uji codec mencakup path tag (cakupan program, member, elemen, padding), nilai bertipe dan overflow, serta pemecahan
Multiple_Service_Packet. Ke-28 vektor bersama mencakup header enkapsulasi dan item CPF, path logis dengan segmen
8/16/32-bit, path simbolik dengan padding, member dan elemen, data Write Tag, balasan CIP dengan additional status,
item ListIdentity, dan masukan rusak dari setiap jenis; kembaran Rust di-fuzz (`enip`).

Uji sesi menjalankan klien terhadap PLC simulasi dalam proses: sesi dan identitas (semua atribut, atribut 7,
ListIdentity), pembacaan tag dengan elemen dan jumlah, tag tak dikenal dan elemen di luar jangkauan,
Multiple_Service_Packet dengan satu tag gagal, penolakan mode hanya-baca, penulisan bertipe, tipe yang salah dan tag
hanya-baca, get/set assembly, permintaan yang di-route lewat Unconnected_Send, ListIdentity lewat UDP, dan SendRRData
tanpa sesi.

## Keterbatasan

Messaging implisit (class 1, I/O siklik) dan messaging terhubung Forward_Open adalah item Fase 3. Struktur Logix
(UDT, STRING), Read Tag Fragmented untuk array besar, penjelajahan tag (Symbol Object), CIP Security, dan DLR belum
diimplementasikan. Adapter tidak meneruskan Unconnected_Send lebih jauh: adapter selalu menjadi tujuan.

## Pelajari lebih lanjut

Notebook `notebooks/industrial/27-ethernetip.id.ipynb` · sampel `samples/console/EnipTags` · [Modbus](modbus.md) ·
[OPC UA](opcua.md)
