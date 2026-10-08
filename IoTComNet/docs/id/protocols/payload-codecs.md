---
title: Codec payload
translation-status: synced
---

# Codec payload: Protobuf, MessagePack, TLV

**Ringkasan.** Transport memindahkan byte; aplikasi menginginkan nilai. `IPayloadCodec<T>` (di Abstractions) adalah
kontrak kecil yang diimplementasikan setiap codec — `ContentType`, `Encode(T)`, `Decode(bytes)` — sehingga jembatan
MQTT atau resource CoAP bisa berganti format tanpa mengubah kode. IoTCom.Net tidak membangun ulang serializer: adapter
membungkus pustaka yang sudah mapan dan menambahkan tampilan tanpa skema yang Anda perlukan saat men-debug lalu lintas.

| Paket | Codec | Tampilan tanpa skema |
|---|---|---|
| `IoTCom.Net.Serialization.Protobuf` | `ProtobufCodec<T>` di atas tipe hasil generate Google.Protobuf; stream length-delimited | `ProtobufWire.TryInspect` / `Describe`: nomor field, wire type, nilai, pesan bersarang |
| `IoTCom.Net.Serialization.MessagePack` | `MessagePackCodec<T>` di atas MessagePack-CSharp (berikan opsi dengan resolver Anda; source-generated untuk NativeAOT) | `MessagePackView.ToJson` / `Describe` |
| `IoTCom.Net.Serialization.Tlv` | `TlvFormat` (tag dan length tetap 1/2/4 byte, urutan byte mana pun) dan `BerTlv` (ISO 8825 / EMV / ISO 7816: tag multi-byte, length panjang, tag constructed) | `BerTlv.Describe` |
| `IoTCom.Net.Serialization.SenML` | `SenMLPayloadCodec(cbor: false \| true)` | — |

Paket TLV termasuk dalam meta-package IoTCom.Net; Protobuf dan MessagePack terpisah agar dependensinya tetap opsional
(Sparkplug B membawa Google.Protobuf sendiri).

## Mulai cepat

```csharp
using IoTCom.Net.Serialization.Protobuf;
using IoTCom.Net.Serialization.MessagePack;
using IoTCom.Net.Serialization.Tlv;

IPayloadCodec<Telemetry> codec = new ProtobufCodec<Telemetry>();          // Telemetry hasil generate protoc / Grpc.Tools
await mqtt.PublishAsync("plant/line1/telemetry", codec.Encode(reading));

ProtobufWire.TryInspect(unknownBytes, out var fields);                    // tidak perlu .proto
foreach (var f in fields) Console.WriteLine($"#{f.Number} {f.Value}");

var json = MessagePackView.ToJson(packedBytes);                           // {"temp":27.5,"rh":71}

var fci = BerTlv.Decode(Convert.FromHexString("6F148407A0000000031010A5095004564953419F3800"));
var label = fci[0].Find(0x50);                                            // "VISA"
var simple = TlvFormat.Simple.Encode([TlvItem.Primitive(0x01, [0x2A])]);  // 01 01 2A
```

## Alat

```bash
iotcom payload protobuf "08 96 01 12 02 68 69"   # pohon field
iotcom payload msgpack 81A174CB4035800000000000   # JSON
iotcom payload ber-tlv 6F14…                      # frame lane + pohon
iotcom payload tlv 01012A02024F4B                 # tag 1 byte, length 1 byte
```

Frame viewer di ekstensi VS Code menyediakan `protobuf`, `msgpack`, dan `ber-tlv` sebagai decoder.

## Pengujian

Uji Protobuf membandingkan byte dengan tipe `Timestamp` yang sudah dikenal, round-trip stream length-delimited, dan
memeriksa pesan bersarang tanpa skema; string yang dapat dicetak ditampilkan sebagai teks. Uji MessagePack memeriksa
byte persis dan tampilan JSON; uji TLV mencakup kedua urutan byte, overrun, FCI EMV dengan tag multi-byte dan length
bentuk panjang.

## Keterbatasan

Inspeksi Protobuf tidak bisa mengetahui nama field atau membedakan field repeated packed dari bytes. BER-TLV tidak
menerima length tak tentu. CBOR di luar SenML belum dicakup.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/12-mdns-sparkplug.id.ipynb` · [SenML](senml.md) · [Sparkplug B](sparkplug.md)
