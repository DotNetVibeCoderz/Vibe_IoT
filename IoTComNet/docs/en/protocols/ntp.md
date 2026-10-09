---
title: NTP / SNTP
translation-status: synced
---

# NTP / SNTP

**Summary.** Devices need correct time to timestamp measurements, order events, rotate logs and check certificates.
Their crystals drift by tens of parts per million, which adds up to seconds a day. NTP (RFC 5905) measures a clock
against a server with four timestamps on UDP port 123. SNTP (RFC 4330) is its simple client form: one request, a few
checks, and the application applies the offset. `IoTCom.Net.Protocols.Ntp` provides:

- `SntpClient`:
  - queries one server or several (`SynchronizeAsync` drops slow answers and takes the median offset, so one wrong
    server is outvoted)
  - applies the RFC 4330 checks: the answer must echo our transmit time (whose low bits are random), come from a server
    in server mode, carry a non-zero transmit time and not raise the leap alarm
  - kiss-o'-death handling (`NtpKissOfDeathException` with RATE, DENY…)
  - a minimum poll interval of 15 s per server by default, as public pools expect
  - a replaceable `Clock`, so it can discipline a device or simulated clock; it never sets the system clock
- `NtpServer`: answers client requests from any clock, with stratum, reference identifier and leap indicator, optional
  rate limiting with kiss-o'-death RATE, and a simulated processing delay.
- `NtpPacket`, `NtpTimestamp` (era-aware: 1968–2104 across the 2036 rollover), `NtpMath` (offset and delay) and the
  frame lane (`NtpPacket.Describe`).
- `DriftingClock`: a clock with an offset and a drift in ppm, for simulations, tests and the Gallery.

The codec has a fuzzed Rust twin (`rust/crates/iotcom-ntp`) checked against the same `/conformance/ntp.json` vectors,
which come from an independent Python reference.

## When to use it

- Checking how far a gateway, PLC or field device is off before trusting its timestamps.
- Keeping an application-level clock (a data logger, a simulator, an embedded device's RTC) in step with a server.
- Serving time on an isolated plant or vessel network from a GPS-disciplined host.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Ntp --prerelease    # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.Ntp;

await using var sntp = SntpClient.Create(o => o.UseServer("pool.ntp.org").UseServer("time.cloudflare.com"));
var estimate = await sntp.SynchronizeAsync();
foreach (var r in estimate.Accepted) Console.WriteLine(r);   // offset, delay, stratum, reference
Console.WriteLine($"local clock is {estimate.Offset.TotalMilliseconds:+0.0} ms off");
```

Disciplining a device clock:

```csharp
var rtc = new DriftingClock(TimeSpan.Zero, driftPpm: 0);   // or wrap your own clock in Clock
await using var sntp = SntpClient.Create(o => { o.UseServer("10.0.0.1"); o.Clock = () => rtc.UtcNow; });
var r = await sntp.QueryAsync();
rtc.Step(r.Offset);
```

A server on a local network:

```csharp
await using var server = NtpServer.Create(o => o.UseUdp(123).WithReference("GPS", stratum: 1));
await server.StartAsync();
```

## Being a good citizen

Public servers are run by volunteers. Query them at most every few minutes for a device and keep the default
`MinimumPollInterval` (15 s). When a server sends kiss-o'-death RATE, back off; on DENY or RSTR stop using it. Serving
time on port 123 needs administrator rights on most systems, and a server you run should advertise
`NtpLeap.Unsynchronised` while it has no good reference.

## Tools

```bash
iotcom ntp query                                    # pool.ntp.org
iotcom ntp query time.cloudflare.com pool.ntp.org
iotcom ntp query --sim --frames                     # three in-process servers, one of them 30 s wrong
iotcom ntp serve --port 1123 --ref LOCL --stratum 10
```

The VS Code extension decodes NTP packets (`ntp`), and pcapng captures use UDP port 123 so Wireshark dissects them.
Gallery: *Fleet clock sync over NTP*.

## Testing

Codec tests cover:
- the Unix epoch as `0x83AA7E80` seconds, half-second fractions and 100 ns round trips;
- the 2036 era boundary, on-wire differences across it, and out-of-range dates;
- request and answer packets with root delay and dispersion, ASCII and IPv4 reference identifiers, kiss-o'-death, a
  MAC trailer, and short packets;
- the RFC 5905 offset and delay arithmetic.

Session tests use an in-memory network with latency:
- a drifting clock measured and corrected to within a few milliseconds;
- rate limiting with kiss-o'-death RATE, an unsynchronised server, and a timeout;
- a forged answer with the wrong originate timestamp ignored before the genuine one is accepted;
- non-client modes left unanswered and the request version echoed;
- three servers where the median outvotes one that is 30 s wrong, plus a silent server reported as failed.

The 25 shared vectors (packets, timestamps across both eras, and exchanges) run in both the C# and the Rust test
suites. The Rust codec was fuzzed for 6.7 million runs with no findings.

## Limitations

This is SNTP: no clock filter, no peer selection beyond the median, no frequency discipline loop, no symmetric or
broadcast associations, and no NTS (RFC 8915) or symmetric-key authentication (the MAC is carried, not checked). It
does not set the operating system clock; use the OS time service for that.

## Learn more

Notebook `notebooks/network/19-ntp.en.ipynb` · sample `samples/console/NtpClock` · [Endpoints and transports](../concepts/endpoints-and-transports.md)
