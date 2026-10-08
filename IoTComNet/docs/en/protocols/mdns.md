---
title: mDNS / DNS-SD
translation-status: synced
---

# mDNS / DNS-SD (zero-configuration discovery)

**Summary.** Multicast DNS (RFC 6762) answers DNS questions on the local link without a DNS server, and DNS-SD
(RFC 6763) uses it to advertise services: a browser asks `224.0.0.251:5353` for a type such as `_mqtt._tcp` and each
instance answers with its name (PTR), host and port (SRV), properties (TXT) and address (A/AAAA). Gateways, printers,
brokers, ESPHome and Shelly devices, PLC web servers and Home Assistant all use it. IoTCom.Net provides:

- `DnsMessage`, a DNS codec with name compression (`Describe` gives the frame lane);
- `MdnsResponder`, which advertises services: announcements, answers to multicast and unicast (QU) questions,
  known-answer suppression, and goodbyes (TTL 0) when a service is withdrawn or the responder stops;
- `MdnsBrowser`, which discovers and resolves them: a TTL cache, re-queries with back-off, service-type enumeration
  (`_services._dns-sd._udp`) and a `ServiceChanged` event for services that appear, change or leave;
- `MdnsSimulator`, a plant segment in memory (Modbus gateway, MQTT broker, CoAP sensor, dashboard, printer).

## When to use it

- Finding devices on a commissioning laptop without knowing their IP addresses.
- Letting your own gateway or edge service announce itself (`_modbus._tcp`, `_mqtt._tcp`, `_http._tcp`).
- Building inventories of what is plugged into a network segment.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.Mdns --prerelease      # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.Mdns;

// Discover
await using var browser = MdnsBrowser.Create();
foreach (var type in await browser.EnumerateTypesAsync(TimeSpan.FromSeconds(2)))
    Console.WriteLine(type);
foreach (var svc in await browser.BrowseAsync("_mqtt._tcp", TimeSpan.FromSeconds(2)))
    Console.WriteLine($"{svc.Instance} {svc.Address}:{svc.Port} {string.Join(" ", svc.Properties)}");

// Advertise
await using var responder = MdnsResponder.Create();
await responder.StartAsync();
await responder.RegisterAsync(new MdnsService
{
    Instance = "Line 1 gateway", Type = "_modbus._tcp", Port = 502,
    Addresses = MdnsAddresses.LocalAddresses(),
    Properties = new Dictionary<string, string> { ["units"] = "1-8" },
});
```

`BrowseContinuouslyAsync(type)` keeps browsing (1 s, 2 s, 4 s … up to 60 s between questions) and raises
`ServiceChanged` with `Lost = true` when a goodbye arrives or a record expires. Without options both endpoints bind
UDP 5353 with address reuse, so they coexist with the operating system's own responder (Bonjour, Avahi, Windows).
`UseInMemory(network, address)` runs them on an `InMemoryDatagramNetwork`, which now delivers multicast to every
member of the group.

## Tools

```bash
iotcom mdns browse                       # every service type, then the instances of each
iotcom mdns browse _modbus._tcp --watch  # keep watching, print joins and goodbyes
iotcom mdns browse --sim                 # simulated plant segment, no network traffic
iotcom mdns advertise "Line 1 gateway" _modbus._tcp 502 --txt units=1-8
iotcom payload dns <hex>                 # decode a captured mDNS packet
```

The Gallery's *Plant network · Sparkplug B* demo starts with an mDNS view of the segment.

## Testing

Codec tests cover name compression and pointers, PTR, SRV, TXT and A records, truncated and malicious input (pointer loops,
counts larger than the message). Endpoint tests run on the in-memory network: announcement, browse and resolve,
TXT properties, known-answer suppression, unicast replies, type enumeration, goodbyes, and the simulator.

## Limitations

No probing and conflict resolution yet (names are assumed unique), no IPv6 multicast group (FF02::FB) and no
DNS-SD over unicast DNS.

## Learn more

Notebook `notebooks/messaging/12-mdns-sparkplug.en.ipynb` · sample `samples/console/MdnsDiscovery` · `iotcom mdns --help`
