---
title: SenML
translation-status: synced
---

# SenML (RFC 8428)

**Summary.** Sensor Measurement Lists: a small standard for sensor payloads, used by LwM2M, many MQTT platforms and
CoAP. IoTCom.Net encodes and decodes JSON and CBOR without reflection (trim/AOT safe) and resolves base fields.

## When to use it

Whenever measurements cross a boundary between vendors or teams: a standard shape beats an ad-hoc JSON object.

## Installation

```bash
dotnet add package IoTCom.Net.Serialization.SenML --prerelease
```

## Quickstart

```csharp
var pack = new SenMLPackBuilder("urn:dev:mac:0024befffe804ff1/")
    .At(DateTimeOffset.UtcNow)
    .Add("temperature", 23.5, "Cel")
    .Add("humidity", 61, "%RH")
    .Add("door", true)
    .Build();

byte[] json = SenMLCodec.ToJson(pack);   // application/senml+json
byte[] cbor = SenMLCodec.ToCbor(pack);   // application/senml+cbor — about a third smaller

foreach (var r in SenMLCodec.Resolve(SenMLCodec.ParseJson(json)))
    Console.WriteLine($"{r.Name} = {r.Value} {r.Unit} @ {r.Time:O}");
```

## Model

`SenMLRecord` mirrors the wire fields (`bn`, `bt`, `bu`, `bv`, `bs`, `bver`, `n`, `u`, `v`, `vs`, `vb`, `vd`, `s`,
`t`, `ut`). `Resolve` applies RFC 8428 §4.6: base name + name, base value + value, base time + time, and converts
relative times (< 2²⁸) against "now".

## Errors

Malformed payloads throw `ProtocolException` with the parser's message.

## Learn more

Notebook `notebooks/messaging/04-mqtt-senml.en.ipynb` · [MQTT](mqtt.md) · the gateway sample publishes SenML.
