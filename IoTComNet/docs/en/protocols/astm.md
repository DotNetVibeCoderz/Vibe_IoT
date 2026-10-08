---
title: ASTM E1394
translation-status: synced
---

# ASTM E1394 / LIS2-A2 (lab analyzers)

**Summary.** Laboratory analyzers send results to the laboratory information system (LIS) with ASTM E1394 / CLSI
LIS2-A2 records (header H, patient P, order O, result R, comment C, query Q, terminator L) over the ASTM E1381 /
LIS1-A link: `ENQ` to start, one framed record at a time (`STX FN text ETX C1 C2 CR LF`), each acknowledged with `ACK`
or rejected with `NAK`, and `EOT` to finish. IoTCom.Net provides:

- `AstmMessage` and `AstmMessageBuilder` (delimiters from the header, typed results);
- `AstmLink` (frames, checksums, splitting records longer than 240 characters with ETB);
- `AstmReceiver` (LIS side) and `AstmSender` (analyzer side, NAK retransmission);
- `AnalyzerSimulator`, a synthetic chemistry analyzer, and `AstmToHl7.ToOru`, a bridge to HL7 v2.5.1 ORU^R01.

Everything here is synthetic and **not a medical device**.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Astm --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart: receive results

```csharp
using IoTCom.Net.Protocols.Astm;

await using var lis = AstmReceiver.Create(o => o.UseTcp(IPAddress.Any, 5000));   // or o.ServeSerial("COM3", 9600)
lis.MessageReceived += (_, m) =>
{
    foreach (var r in m.Results) Console.WriteLine($"{m.SpecimenId} {r.TestCode} {r.Value} {r.Units} {r.Flag}");
};
await lis.StartAsync();
```

## Bridge to HL7

```csharp
await using var his = Hl7MllpClient.Create(o => o.UseTcp("10.0.0.20", 2575));
lis.MessageReceived += async (_, m) => await his.SendAsync(AstmToHl7.ToOru(m));
```

The `AstmAnalyzerBridge` sample does this end to end, including a corrupted frame that is retransmitted.

## Tools

```bash
iotcom astm listen --port 5000 --hl7     # print results and their ORU^R01
iotcom astm send --port 5000 -n 3        # synthetic chemistry results
dotnet run --project samples/console/AstmAnalyzerBridge
```

## Testing

Records and results (with the header's delimiters, custom delimiters included), frame checksums and record
splitting, a transfer with a corrupted frame (one NAK, one retransmission, identical message at the LIS) and the
HL7 bridge (parsed back with the HL7 codec).

## Limitations

Not yet included: queries (Q) answered by the LIS (host query mode), line contention resolution when both sides send
ENQ at once (the receiver simply accepts), and vendor-specific record extensions (M, S records are kept as raw records).

## Learn more

Notebook `notebooks/devices/11-at-astm.en.ipynb` · [HL7 v2](hl7.md) · `iotcom astm --help`
