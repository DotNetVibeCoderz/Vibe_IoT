---
title: mDNS / DNS-SD
translation-status: synced
---

# mDNS / DNS-SD (penemuan tanpa konfigurasi)

**Ringkasan.** Multicast DNS (RFC 6762) menjawab pertanyaan DNS di link lokal tanpa server DNS, dan DNS-SD
(RFC 6763) memakainya untuk mengiklankan layanan: browser bertanya ke `224.0.0.251:5353` untuk tipe seperti
`_mqtt._tcp` dan setiap instance menjawab dengan nama (PTR), host dan port (SRV), properti (TXT), serta alamat
(A/AAAA). Gateway, printer, broker, perangkat ESPHome dan Shelly, web server PLC, dan Home Assistant semuanya
memakainya. IoTCom.Net menyediakan:

- `DnsMessage`, codec DNS dengan kompresi nama (`Describe` memberi frame lane);
- `MdnsResponder`, yang mengiklankan layanan: pengumuman, jawaban untuk pertanyaan multicast dan unicast (QU),
  known-answer suppression, dan goodbye (TTL 0) saat layanan ditarik atau responder berhenti;
- `MdnsBrowser`, yang menemukan dan meresolusinya: cache TTL, pertanyaan ulang dengan back-off, enumerasi tipe
  layanan (`_services._dns-sd._udp`), dan event `ServiceChanged` untuk layanan yang muncul, berubah, atau pergi;
- `MdnsSimulator`, segmen pabrik di memori (gateway Modbus, broker MQTT, sensor CoAP, dasbor, printer).

## Kapan dipakai

- Menemukan perangkat dari laptop commissioning tanpa tahu alamat IP-nya.
- Membiarkan gateway atau layanan edge Anda sendiri mengumumkan dirinya (`_modbus._tcp`, `_mqtt._tcp`, `_http._tcp`).
- Membuat inventaris apa saja yang tersambung ke suatu segmen jaringan.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Mdns --prerelease      # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Mdns;

// Menemukan
await using var browser = MdnsBrowser.Create();
foreach (var type in await browser.EnumerateTypesAsync(TimeSpan.FromSeconds(2)))
    Console.WriteLine(type);
foreach (var svc in await browser.BrowseAsync("_mqtt._tcp", TimeSpan.FromSeconds(2)))
    Console.WriteLine($"{svc.Instance} {svc.Address}:{svc.Port} {string.Join(" ", svc.Properties)}");

// Mengiklankan
await using var responder = MdnsResponder.Create();
await responder.StartAsync();
await responder.RegisterAsync(new MdnsService
{
    Instance = "Line 1 gateway", Type = "_modbus._tcp", Port = 502,
    Addresses = MdnsAddresses.LocalAddresses(),
    Properties = new Dictionary<string, string> { ["units"] = "1-8" },
});
```

`BrowseContinuouslyAsync(type)` terus menjelajah (1 dtk, 2 dtk, 4 dtk … hingga 60 dtk di antara pertanyaan) dan
memicu `ServiceChanged` dengan `Lost = true` saat goodbye tiba atau record kedaluwarsa. Tanpa opsi, kedua endpoint
mengikat UDP 5353 dengan address reuse, sehingga bisa berdampingan dengan responder milik sistem operasi (Bonjour,
Avahi, Windows). `UseInMemory(network, address)` menjalankannya di `InMemoryDatagramNetwork`, yang kini mengirim
multicast ke setiap anggota grup.

## Alat

```bash
iotcom mdns browse                       # setiap tipe layanan, lalu instance masing-masing
iotcom mdns browse _modbus._tcp --watch  # terus memantau, cetak yang bergabung dan goodbye
iotcom mdns browse --sim                 # segmen pabrik simulasi, tanpa lalu lintas jaringan
iotcom mdns advertise "Line 1 gateway" _modbus._tcp 502 --txt units=1-8
iotcom payload dns <hex>                 # urai paket mDNS hasil tangkapan
```

Demo *Jaringan pabrik · Sparkplug B* di Gallery dimulai dengan tampilan mDNS dari segmen tersebut.

## Pengujian

Uji codec mencakup kompresi nama dan pointer, record PTR, SRV, TXT, dan A, masukan terpotong dan berbahaya (loop pointer,
jumlah melebihi ukuran pesan). Uji endpoint berjalan di jaringan in-memory: pengumuman, browse dan resolve, properti
TXT, known-answer suppression, balasan unicast, enumerasi tipe, goodbye, dan simulator.

## Keterbatasan

Belum ada probing dan resolusi konflik (nama dianggap unik), belum ada grup multicast IPv6 (FF02::FB), dan belum ada
DNS-SD lewat DNS unicast.

## Pelajari lebih lanjut

Notebook `notebooks/messaging/12-mdns-sparkplug.id.ipynb` · sampel `samples/console/MdnsDiscovery` · `iotcom mdns --help`
