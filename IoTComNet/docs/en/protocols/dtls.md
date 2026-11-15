---
title: DTLS
translation-status: synced
---

# DTLS 1.2 (coaps://)

**Summary.** DTLS is TLS for datagrams: the same handshake and record protection, with retransmission timers,
sequence numbers and a cookie exchange so it works over UDP. CoAP (RFC 7252 §9) and LwM2M secure their traffic with it
on UDP 5684 (`coaps://`). `IoTCom.Net.Transport.Dtls` is an adapter over BouncyCastle (MIT), the most widely used
managed DTLS stack, and provides:

- `DtlsClientTransport` and `DtlsServerTransport`: `IDatagramTransport`s that encrypt everything sent and decrypt
  everything received, so any datagram protocol (CoAP, LwM2M, your own) runs unchanged.
- PSK mode: `TLS_PSK_WITH_AES_128_CCM_8` (the mandatory suite for constrained devices, RFC 7925) and
  `TLS_PSK_WITH_AES_128_GCM_SHA256`, with a server-side identity lookup (`PskLookup`, `UsePsk`).
- Certificate mode: `TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8` and `..._AES_128_GCM_SHA256`, with the client accepting the
  server by pinned SHA-256 fingerprint or by a chain to roots you supply (no OS trust store; plant networks rarely have
  a public CA). `DtlsCertificates.CreateSelfSigned` makes a P-256 certificate for labs and pinned deployments.
- Server: HelloVerifyRequest cookies before any work (no amplification), one session per peer address, idle expiry,
  `Sessions`, `SessionEstablished` (cipher suite, PSK identity) and `HandshakeFailed` events.
- `UseDtlsClient` / `UseDtlsServer` builder extensions for every datagram endpoint.

Following the design (curation tier 2), DTLS is wrapped, not rebuilt: BouncyCastle carries the cryptography and the
handshake state machine, and IoTCom adds the transport model, demultiplexing and diagnostics.

## When to use it

- A CoAP device or LwM2M server that requires `coaps://` (Leshan, Eclipse Wakaama, Californium, nRF/Zephyr devices).
- Securing your own UDP protocol without designing a key exchange.
- Testing a device's DTLS profile in-process (PSK identity, suites, wrong-key handling, lossy links).

## Installation

```bash
dotnet add package IoTCom.Net.Transport.Dtls --prerelease    # separate package: not in the meta-package
```

## Quickstart

CoAP over DTLS with a pre-shared key:

```csharp
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Transport.Dtls;

var server = new IPEndPoint(IPAddress.Parse("192.168.1.40"), 5684);
var psk = DtlsPsk.FromHex("greenhouse-node-01", Environment.GetEnvironmentVariable("NODE_PSK")!);
await using var coap = CoapClient.Create(o => o
    .UseDtlsClient(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0)), server, new DtlsClientOptions { Psk = psk })
    .UseServer(server));
await coap.ConnectAsync();
Console.WriteLine((await coap.GetAsync("/sensors/temperature")).PayloadText);
```

A server with both PSK and a certificate:

```csharp
var options = new DtlsServerOptions { Certificate = DtlsCertificates.CreateSelfSigned("gw.plant.local") }
    .UsePsk(new Dictionary<string, byte[]> { ["node-01"] = key1, ["node-02"] = key2 });
await using var server = CoapServer.Create(o => o.UseDtlsServer(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 5684)), options));
```

A client that pins the server certificate:

```csharp
var pinned = new DtlsClientOptions();
pinned.PinnedFingerprints.Add("353D674FFD7C5F4DA1BFB19E98A4C0ADD6E521835EC10FBB6A6442BC853D2604");
var dtls = new DtlsClientTransport(new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0)), server, pinned);
await dtls.HandshakeAsync();            // throws with the reason: untrusted certificate, refused PSK, timeout
Console.WriteLine(dtls.CipherSuite);    // EcdheEcdsaAes128Ccm8
```

LwM2M uses the same extensions (`Lwm2mClient`/`Lwm2mServer` options are datagram builders).

## Safety

Keys are secrets. `DtlsPsk.ToString()` never shows the key, IoTCom never logs it, and the CLI reads it from
`IOTCOM_DTLS_PSK` so it stays out of shell history. Certificate mode without pins or roots refuses every server — there
is no "accept anything" switch. A record with a bad MAC is dropped silently, as RFC 6347 requires, so a wrong key
shows up as a handshake timeout rather than an alert; the error message says what to check.

## Tools

```bash
export IOTCOM_DTLS_PSK=node-01:000102030405060708090A0B0C0D0E0F
iotcom coap serve --psk node-01:000102030405060708090A0B0C0D0E0F     # greenhouse node on coaps://0.0.0.0:5684
iotcom coap get coaps://127.0.0.1/sensors/temperature                 # PSK from IOTCOM_DTLS_PSK
iotcom coap serve --cert                                              # self-signed certificate, prints its fingerprint
iotcom coap discover coaps://127.0.0.1/ --pin 353D674F…2604
```

Every `coap` command accepts `coaps://` with `--psk` or `--pin`. Captures and `--frames` show the decrypted CoAP
messages; the encrypted records stay on the wire.

## Testing

Tests run CoAP over DTLS in memory: a PSK session (GET, PUT), the negotiated suite and the PSK identity the server
saw, a wrong key and an unknown identity refused, certificate mode with a pinned fingerprint and with a trusted root,
a wrong pin refused (and the failure reported again on later calls), a handshake under 20 % packet loss, and an LwM2M
client registering over DTLS. The `CoapsSecure` sample runs the same over real UDP.

## Limitations

DTLS 1.3 (RFC 9147) is not offered yet: BouncyCastle's managed stack does not complete it, and the alternative is a
native OpenSSL build; it is planned when a managed implementation is available. Connection IDs (RFC 9146), client
certificates (mutual TLS), raw public keys (RFC 7250), session resumption and OSCORE are not implemented.

## Learn more

Notebook `notebooks/network/28-dtls.en.ipynb` · sample `samples/console/CoapsSecure` · [CoAP](coap.md) ·
[LwM2M](lwm2m.md)
