---
title: HL7 v2 over MLLP
translation-status: synced
---

# HL7 v2 over MLLP

**Summary.** HL7 version 2 is the message format most hospital devices and systems still use: bedside monitors, lab
analyzers, admission systems and interface engines. Messages are ER7 text (`MSH|^~\&|…`, one segment per line) sent
over TCP with **MLLP** framing (`0x0B` … `0x1C 0x0D`). Every message is answered with an ACK. IoTCom.Net parses,
builds, sends, receives and acknowledges HL7 v2 messages. It also includes a bedside-monitor simulator with fictional
patients.

> **Not a medical device.** IoTCom.Net is a communication library. It is not certified for diagnosis, monitoring or
> treatment decisions. The simulator and the demos use fictional patients only.

## When to use it

- Collecting vital signs from bedside monitors or a central station that exports HL7 v2 (ORU^R01).
- Receiving results from lab analyzers or middleware, or ADT events from a hospital information system.
- Building an edge gateway that forwards device data to MQTT, a database or a FHIR server.
- Testing an interface engine against a realistic device feed without hardware.

## Roles

| Role | Type |
|---|---|
| Receiver / server / subscriber | `Hl7MllpServer` — many connections, automatic ACK (AA, or AR for unparseable input), `MessageReceived`, `ReceiveAsync`, `SubscribeAsync("ORU^R01")` |
| Sender / client / publisher | `Hl7MllpClient` — `SendAsync` waits for the ACK whose MSA-2 matches the control ID |
| Codec | `Hl7Message`, `Hl7MessageBuilder`, `Hl7Escaping`, `Hl7Time`, `MllpFraming` (sans-I/O) |
| Clinical helpers | `Hl7Patient`, `Hl7Observation`, `VitalSigns` (LOINC codes), `GetPatient()`, `GetObservations()` |
| Simulator | `PatientMonitorSimulator` — stable, sepsis, hypoxia and hypertension courses; `DemoWard` |

## Transports

`UseTcp(IPAddress.Any, 2575)` on the receiver and `UseTcp(host, 2575)` on the sender. Port 2575 is the registered
HL7 port; many devices use another port, so check the vendor's interface specification. `UseInMemory(listener)` is
available for tests.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Hl7 --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.Hl7;

await using var receiver = Hl7MllpServer.Create(o => o.UseTcp(IPAddress.Any, 2575));
receiver.MessageReceived += (_, e) =>
{
    var patient = e.Message.GetPatient();
    foreach (var obs in e.Message.GetObservations())
        Console.WriteLine($"{patient?.DisplayName}: {obs.Code.Text} = {obs.Value} {obs.Units} {obs.AbnormalFlag}");
};   // an ACK AA is sent automatically after the handler returns
await receiver.StartAsync();
```

## Reading messages

Field numbering follows the standard. MSH-1 is the field separator, so MSH-9 is the message type. The indexer
accepts a terser-like path:

```csharp
var m = Hl7Message.Parse(text);                 // CR, LF or CRLF segment separators
m.MessageType                                   // "ORU^R01"
m["PID.5.1"]                                    // family name
m["OBX(2).5"]                                   // value of the second OBX
m.GetSegments("OBX").Count
Hl7Escaping.Unescape(@"Fever \T\ chills", Hl7Delimiters.Default)   // "Fever & chills"; \F\ \S\ \R\ \E\ .br and \Xhh\ are supported too
```

## Building messages

```csharp
var oru = new Hl7MessageBuilder()
    .Header("MONITOR", "ICU", "IOTCOM", "HOSP", "ORU^R01^ORU_R01")
    .Patient(new Hl7Patient { Id = "MRN-001", FamilyName = "Santoso", GivenName = "Budi", Sex = "M" })
    .Segment("OBR", "1", "", "", "VITALS^Vital signs")
    .Observation(1, new Hl7Observation
    {
        Code = VitalSigns.HeartRate, Value = "118", Units = "/min",
        ReferenceRange = "60-100", AbnormalFlag = "H", Timestamp = DateTimeOffset.Now,
    })
    .Build();

var ack = await sender.SendAsync(oru);          // throws DeviceException on AE/AR; pass throwOnNegativeAck: false to inspect
Console.WriteLine(ack.AckCode());               // "AA"
```

`Segment(...)` escapes every value except the component separator `^`, so `"VITALS^Vital signs"` keeps its two
components.

## Custom acknowledgements

Set `e.Ack` in the handler to answer with something other than AA. Use `WithoutAutoAck()` when your code sends
acknowledgements through another path.

```csharp
receiver.MessageReceived += (_, e) =>
{
    if (string.IsNullOrEmpty(e.Message.GetPatient()?.Id))
        e.Ack = e.Message.CreateAck("AE", "PID-3 patient identifier is required");
};
```

## Configuration

| Option | Default | Description |
|---|---|---|
| `Encoding` | UTF-8 | text encoding (older devices often use Latin-1 — set `Encoding.Latin1`) |
| `AutoAcknowledge` / `WithoutAutoAck()` | on | the server answers every message |
| `AckTimeout` / `WithAckTimeout(...)` | 5 s | client: how long to wait for the ACK (`IoTComTimeoutException`) |
| `MllpFraming(maxMessageLength)` | 1 MiB | larger frames are rejected (protects against unterminated streams) |

## Simulator

`PatientMonitorSimulator(patient, scenario, bed, seed, onset)` produces physiologically plausible vitals: HR, RR,
SpO₂, NIBP and temperature, with noise and a deterioration that starts at `onset`. `ToOru(sample)` adds reference
ranges and H/L/N flags; `Admission(now)` produces an ADT^A01; `FromOru(message)` reads a sample back.
`PatientMonitorSimulator.DemoWard` provides four fictional patients, one per scenario.

```bash
iotcom hl7 listen                                   # MLLP receiver on 2575 with decoded observations
iotcom hl7 simulate --scenario sepsis --interval 2  # a synthetic bedside monitor
iotcom hl7 send message.hl7                         # send a file and print the ACK
dotnet run --project samples/console/Hl7MllpListener -- --simulate
```

## Testing and interoperability

Tests cover delimiter parsing, escape sequences, repetitions and sub-components, MLLP fragments and multiple frames
in one read, ACK matching, AE/AR handling and a full simulator → MLLP → receiver round trip. The parser is tolerant of
LF line endings and trailing empty segments, which are common in files exported from real interface engines.

## Security

MLLP has no authentication or encryption, and HL7 messages contain personal health information. Use HL7 only on
segregated clinical networks or over a TLS tunnel/VPN. Log metadata (type, control ID) rather than full messages,
and treat every field as untrusted input.

## Limitations

HL7 v2 only (v2.3–v2.8 ER7 encoding); XML encoding and HL7 v3 are out of scope. For FHIR, use the Firely SDK
together with IoTCom.Net. Conformance profiles and Z-segment validation are left to the application.

## Learn more

Notebook `notebooks/medical/05-hl7-dicom.en.ipynb` · Gallery demo *ICU bedside monitors* ·
[Medical AI guide](../guides/medical-ai.md) · [DICOM](dicom.md) · `iotcom hl7 --help`
