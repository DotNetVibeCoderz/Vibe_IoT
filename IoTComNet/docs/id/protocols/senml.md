---
title: SenML
translation-status: synced
---

# SenML (RFC 8428)

**Ringkasan.** Sensor Measurement Lists: standar ringkas untuk payload sensor, dipakai LwM2M, banyak platform MQTT,
dan CoAP. IoTCom.Net meng-encode dan men-decode JSON serta CBOR tanpa reflection (aman untuk trim/AOT) dan
menyelesaikan field dasar (base field).

## Kapan dipakai

Kapan pun pengukuran melintasi batas antar-vendor atau antar-tim: bentuk standar lebih baik daripada objek JSON ad-hoc.

## Instalasi

```bash
dotnet add package IoTCom.Net.Serialization.SenML --prerelease
```

## Mulai cepat

```csharp
var pack = new SenMLPackBuilder("urn:dev:mac:0024befffe804ff1/")
    .At(DateTimeOffset.UtcNow)
    .Add("temperature", 23.5, "Cel")
    .Add("humidity", 61, "%RH")
    .Add("door", true)
    .Build();

byte[] json = SenMLCodec.ToJson(pack);   // application/senml+json
byte[] cbor = SenMLCodec.ToCbor(pack);   // application/senml+cbor — sekitar sepertiga lebih kecil

foreach (var r in SenMLCodec.Resolve(SenMLCodec.ParseJson(json)))
    Console.WriteLine($"{r.Name} = {r.Value} {r.Unit} @ {r.Time:O}");
```

## Model

`SenMLRecord` mencerminkan field di jalur (`bn`, `bt`, `bu`, `bv`, `bs`, `bver`, `n`, `u`, `v`, `vs`, `vb`, `vd`,
`s`, `t`, `ut`). `Resolve` menerapkan RFC 8428 §4.6: base name + name, base value + value, base time + time, serta
mengonversi waktu relatif (< 2²⁸) terhadap "sekarang".

## Error

Payload yang rusak melempar `ProtocolException` dengan pesan dari parser.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/04-mqtt-senml.id.ipynb` · [MQTT](mqtt.md) · sampel gateway menerbitkan SenML.
