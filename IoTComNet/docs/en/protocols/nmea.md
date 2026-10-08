---
title: NMEA 0183
translation-status: synced
---

# NMEA 0183

**Summary.** The ASCII sentence protocol of GPS/GNSS receivers, AIS transponders, depth sounders and marine
instruments: `$GPGGA,123519,4807.038,N,...*47`. IoTCom.Net parses, validates, builds and serves it, and consolidates
sentences into a single GNSS fix.

## When to use it

- Reading position, speed, time and satellite data from a GPS module (serial or USB).
- Consuming NMEA-over-TCP/UDP from chart plotters and marine gateways.
- Emulating a GPS for testing navigation software.

## Roles

| Role | Type |
|---|---|
| Reader / subscriber | `NmeaReader` — typed messages, `IAsyncEnumerable`, `GnssState` |
| Server / publisher | `NmeaServer` — broadcasts to many TCP clients or a serial port |
| Simulator | `NmeaSimulator` — GGA, RMC, VTG, GSA, GSV on a circular track |
| Codec | `NmeaSentence`, `NmeaParser` |

Typed sentences: GGA, RMC, GSA, GSV, VTG, GLL, ZDA (others arrive as `UnknownNmeaMessage` with raw fields).

## Transports

`UseSerial("COM4", 9600)` (most receivers: 9600 or 4800 baud, 8N1), `UseTcp(host, 10110)`, `UseInMemory(listener)`.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Nmea --prerelease
```

## Quickstart

```csharp
await using var gps = NmeaReader.Create(o => o.UseSerial("COM4", 9600));
await gps.ConnectAsync();
await foreach (var rmc in gps.ReadAsync<RmcMessage>())
{
    var fix = gps.Gnss.Current;
    Console.WriteLine($"{fix.Latitude:F6}, {fix.Longitude:F6} · {fix.SpeedKmh:F1} km/h · {fix.SatellitesUsed} sats");
}
```

## Configuration

| Option | Default | Description |
|---|---|---|
| `AcceptInvalidChecksums()` | off | keep sentences with a wrong checksum (flagged via `ChecksumValid`) |
| `WithLogger(...)` | — | logging |

## Examples

```csharp
// Parse and build without any transport
var gga = (GgaMessage)NmeaParser.Parse("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47")!;
string sentence = NmeaSentence.Build("GP", "GLL", "4916.45", "N", "12311.12", "W", "225444", "A");

// Log replay: feed recorded lines through the same pipeline
foreach (var line in File.ReadLines("track.nmea")) gps.Process(line);

// A GPS emulator other apps can connect to (tcp://0.0.0.0:10110)
await using var server = NmeaServer.Create(o => o.UseTcp(IPAddress.Any, 10110));
await server.StartAsync();
await new NmeaSimulator(latitude: -6.9147, longitude: 107.6098).RunAsync(server);
```

## Simulator

`NmeaSimulator(latitude, longitude, radiusMeters, speedKmh, seed)` drives a vehicle on a circle and emits a full epoch
per tick. `GenerateEpoch(utc, elapsed)` is deterministic for tests. CLI: `iotcom nmea simulate --port 10110`.

## Testing and interoperability

Tests use canonical sentences from the NMEA reference examples, coordinate round-trips, checksum rejection and an
end-to-end simulator → server → reader run.

## Security

NMEA has no authentication; anyone on the link can inject positions. Treat positions as untrusted input in
safety-relevant systems and cross-check with other sensors.

## AIS

AIS transponders and receivers send `!AIVDM` (other ships) and `!AIVDO` (own ship) sentences. The binary message is
armoured six bits per character, and long messages are split over several sentences. `AisDecoder` reassembles the
fragments, de-armours the bits and decodes types 1–3 and 27 (positions), 4 (base stations), 5 (static and voyage
data), 18/19 (class B), 21 (aids to navigation) and 24 (class B static data). `AisTracker` keeps a vessel table, and
`AisSimulator` produces traffic in Jakarta Bay.

```csharp
var ais = new AisDecoder();
var tracker = new AisTracker();
await foreach (var line in reader.ReadLinesAsync())          // or any source of NMEA lines (UDP, TCP, serial)
    if (ais.Feed(line) is { } message) tracker.Apply(message);
foreach (var v in tracker.Vessels) Console.WriteLine(v);       // 525005123 KM SINAR JAKARTA -5.9831,106.8602 11.4 kn
```

`AisBits.EncodePosition` and `AisBits.EncodeStatic` build sentences for tests and simulators. The CLI decodes
sentences (`iotcom nmea ais decode`) and shows a live vessel table from a simulator, a UDP port or a TCP feed
(`iotcom nmea ais watch --udp 10110`). Tests pin the published examples (a type 1 report and the two-sentence type 5
of *EVER DIADEM*), cross-checked with an independent decoder.

![Harbour traffic](../../images/gallery-ais.png)

## Limitations

AIS binary application messages (types 6, 8, 25, 26) are reported as unknown types, and AIS is decoded but not
encoded for every type. Two-digit years in RMC use the pivot 80 (80–99 → 19xx, 00–79 → 20xx).

## Learn more

Notebook `notebooks/navigation/03-nmea.en.ipynb` · Gallery demos *GNSS vehicle tracker* and *Harbour traffic (AIS)* · `iotcom nmea --help`
