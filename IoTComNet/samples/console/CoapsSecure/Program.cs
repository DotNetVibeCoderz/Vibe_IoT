// CoapsSecure — CoAP over DTLS 1.2 (coaps://, RFC 7252 §9): a pre-shared key and a pinned certificate.
//
//   dotnet run                                          # greenhouse node on UDP 5684 (PSK) + 5685 (certificate), then a client for each
//   dotnet run -- 192.168.1.40 node-01 000102...0F      # a real coaps server with a PSK (identity, hex key)
//
// Keys are secrets: in real deployments read them from a vault or environment variable, never from logs.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Transport.Dtls;
using IoTCom.Net.Transports;

if (args.Length >= 3)
{
    var remote = new IPEndPoint(IPAddress.Parse(args[0]), args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 5684);
    await using var real = CoapClient.Create(o => o
        .UseDtlsClient(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0)), remote, new DtlsClientOptions { Psk = DtlsPsk.FromHex(args[1], args[2]) })
        .UseServer(remote));
    await real.ConnectAsync();
    foreach (var link in await real.DiscoverAsync()) Console.WriteLine($"{link.Path,-28} {link.ResourceType}");
    return;
}

// 1. PSK — the mandatory profile for constrained devices (TLS_PSK_WITH_AES_128_CCM_8, RFC 7925).
var psk = DtlsPsk.FromHex("greenhouse-node-01", "6A2F0C9B1D4E7F3A8B5C2D1E0F9A8B7C");
var pskServerAt = new IPEndPoint(IPAddress.Loopback, 5684);
await using (var server = CoapServer.Create(o => o.UseDtlsServer(() => new UdpDatagramTransport(pskServerAt), new DtlsServerOptions().UsePsk(psk))))
await using (var device = new CoapDeviceSimulator(server))
{
    await server.StartAsync();
    await using var client = CoapClient.Create(o => o
        .UseDtlsClient(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Loopback, 0)), pskServerAt, new DtlsClientOptions { Psk = psk })
        .UseServer(pskServerAt));
    await client.ConnectAsync();
    Console.WriteLine($"PSK  coaps://{pskServerAt}  temperature = {(await client.GetAsync("/sensors/temperature")).PayloadText} °C");
}

// 2. Certificates — ECDHE_ECDSA with the server's fingerprint pinned (no public CA needed on a plant network).
using var cert = DtlsCertificates.CreateSelfSigned("greenhouse-gw.local");
var certServerAt = new IPEndPoint(IPAddress.Loopback, 5685);
await using (var server = CoapServer.Create(o => o.UseDtlsServer(() => new UdpDatagramTransport(certServerAt), new DtlsServerOptions { Certificate = cert })))
await using (var device = new CoapDeviceSimulator(server))
{
    await server.StartAsync();
    var pinned = new DtlsClientOptions();
    pinned.PinnedFingerprints.Add(DtlsCertificates.Fingerprint(cert));
    var transport = new DtlsClientTransport(new UdpDatagramTransport(new IPEndPoint(IPAddress.Loopback, 0)), certServerAt, pinned);
    await transport.HandshakeAsync();
    Console.WriteLine($"CERT coaps://{certServerAt}  suite {transport.CipherSuite}, pin {DtlsCertificates.Fingerprint(cert)[..16]}…");
    await using var client = CoapClient.Create(o => o.UseDatagramTransport(() => transport).UseServer(certServerAt));
    await client.ConnectAsync();
    Console.WriteLine($"     soil moisture = {(await client.GetAsync("/sensors/soil")).PayloadText} %");

    // A client that pins another fingerprint is refused before any CoAP byte is exchanged.
    var wrong = new DtlsClientOptions { HandshakeTimeout = TimeSpan.FromSeconds(3) };
    wrong.PinnedFingerprints.Add(new string('0', 64));
    await using var intruder = new DtlsClientTransport(new UdpDatagramTransport(new IPEndPoint(IPAddress.Loopback, 0)), certServerAt, wrong);
    try
    {
        await intruder.HandshakeAsync();
    }
    catch (IoTComException ex)
    {
        Console.WriteLine($"     wrong pin -> {ex.Message}");
    }
}
