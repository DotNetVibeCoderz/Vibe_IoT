using System.Net;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Protocols.Lwm2m;
using IoTCom.Net.Transport.Dtls;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Transport;

public sealed class DtlsTests
{
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.50"), 5684);
    private static readonly DtlsPsk Psk = DtlsPsk.FromHex("greenhouse-node-01", "6A2F0C9B1D4E7F3A8B5C2D1E0F9A8B7C");
    private static readonly CoapTransmission Fast = new() { AckTimeout = TimeSpan.FromMilliseconds(200), MaxRetransmit = 6, ResponseTimeout = TimeSpan.FromSeconds(8) };

    private static async Task<(CoapServer Server, CoapDeviceSimulator Device, InMemoryDatagramNetwork Net)> ServerAsync(DtlsServerOptions options)
    {
        var net = new InMemoryDatagramNetwork(seed: 11);
        var server = CoapServer.Create(o => o.UseDtlsServer(() => net.Bind(ServerAddress), options).Transmission = Fast);
        var device = new CoapDeviceSimulator(server);
        await server.StartAsync();
        return (server, device, net);
    }

    private static CoapClient Client(InMemoryDatagramNetwork net, DtlsClientOptions options) => CoapClient.Create(o =>
    {
        o.UseDtlsClient(() => net.Bind(), ServerAddress, options).UseServer(ServerAddress);
        o.Transmission = Fast;
    });

    [Fact]
    public async Task Coap_over_psk()
    {
        var (server, device, net) = await ServerAsync(new DtlsServerOptions().UsePsk(Psk));
        await using (server)
        await using (device)
        await using (var client = Client(net, new DtlsClientOptions { Psk = Psk }))
        {
            await client.ConnectAsync();
            Assert.Equal("27.5", (await client.GetAsync("/sensors/temperature")).PayloadText);
            Assert.Equal(CoapCode.Changed, (await client.PutAsync("/actuators/fan", "on")).Code);
            Assert.True(device.FanOn);
        }
    }

    [Fact]
    public async Task Transport_reports_suite_and_session()
    {
        var net = new InMemoryDatagramNetwork(seed: 3);
        var options = new DtlsServerOptions().UsePsk(Psk);
        await using var server = new DtlsServerTransport(net.Bind(ServerAddress), options);
        DtlsSession? established = null;
        server.SessionEstablished += s => established = s;
        await using var client = new DtlsClientTransport(net.Bind(), ServerAddress, new DtlsClientOptions { Psk = Psk });
        await client.SendAsync("hello"u8.ToArray(), ServerAddress);
        var got = await server.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hello"u8.ToArray(), got.Data.ToArray());
        await server.SendAsync("world"u8.ToArray(), got.Remote);
        Assert.Equal("world"u8.ToArray(), (await client.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Data.ToArray());
        Assert.Equal("PskAes128Ccm8", client.CipherSuite);
        Assert.NotNull(established);
        Assert.Equal("greenhouse-node-01", established!.PskIdentity);
        Assert.Single(server.Sessions);
    }

    [Fact]
    public async Task Wrong_psk_fails_the_handshake()
    {
        var net = new InMemoryDatagramNetwork(seed: 5);
        // A record with a bad MAC is dropped silently (RFC 6347 4.1.2.7), so the server gives up when its own timeout expires.
        await using var server = new DtlsServerTransport(net.Bind(ServerAddress), new DtlsServerOptions { HandshakeTimeout = TimeSpan.FromSeconds(2) }.UsePsk(Psk));
        string? reason = null;
        server.HandshakeFailed += (_, r) => reason = r;
        var wrong = DtlsPsk.FromHex("greenhouse-node-01", "00112233445566778899AABBCCDDEEFF");
        await using var client = new DtlsClientTransport(net.Bind(), ServerAddress, new DtlsClientOptions { Psk = wrong, HandshakeTimeout = TimeSpan.FromSeconds(3) });
        await Assert.ThrowsAnyAsync<IoTComException>(() => client.HandshakeAsync());
        Assert.Empty(server.Sessions);
        for (var i = 0; i < 250 && reason is null; i++) await Task.Delay(20);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task Unknown_identity_is_refused()
    {
        var net = new InMemoryDatagramNetwork(seed: 6);
        await using var server = new DtlsServerTransport(net.Bind(ServerAddress), new DtlsServerOptions().UsePsk(Psk));
        var stranger = DtlsPsk.FromHex("intruder", "6A2F0C9B1D4E7F3A8B5C2D1E0F9A8B7C");
        await using var client = new DtlsClientTransport(net.Bind(), ServerAddress, new DtlsClientOptions { Psk = stranger, HandshakeTimeout = TimeSpan.FromSeconds(3) });
        await Assert.ThrowsAnyAsync<IoTComException>(() => client.HandshakeAsync());
    }

    [Fact]
    public async Task Certificate_mode_with_pinned_fingerprint()
    {
        using var cert = DtlsCertificates.CreateSelfSigned("gw.greenhouse.local");
        var (server, _, net) = await ServerAsync(new DtlsServerOptions { Certificate = cert });
        await using (server)
        {
            var good = new DtlsClientOptions();
            good.PinnedFingerprints.Add(DtlsCertificates.Fingerprint(cert));
            await using (var client = Client(net, good))
            {
                await client.ConnectAsync();
                Assert.Equal("27.5", (await client.GetAsync("/sensors/temperature")).PayloadText);
            }

            var bad = new DtlsClientOptions { HandshakeTimeout = TimeSpan.FromSeconds(3) };
            bad.PinnedFingerprints.Add(new string('0', 64));
            await using var raw = new DtlsClientTransport(net.Bind(), ServerAddress, bad);
            var ex = await Assert.ThrowsAnyAsync<IoTComException>(() => raw.HandshakeAsync());
            Assert.Contains("not trusted", ex.Message);
            // A failed session stays failed and reports the same cause.
            Assert.Same(ex, await Assert.ThrowsAnyAsync<IoTComException>(() => raw.ReceiveAsync().AsTask()));
        }
    }

    [Fact]
    public async Task Certificate_mode_with_trusted_root()
    {
        using var cert = DtlsCertificates.CreateSelfSigned("gw.greenhouse.local");
        var net = new InMemoryDatagramNetwork(seed: 8);
        await using var server = new DtlsServerTransport(net.Bind(ServerAddress), new DtlsServerOptions { Certificate = cert });
        var options = new DtlsClientOptions();
        options.TrustedRoots.Add(cert);
        await using var client = new DtlsClientTransport(net.Bind(), ServerAddress, options);
        await client.HandshakeAsync();
        Assert.StartsWith("EcdheEcdsa", client.CipherSuite);
    }

    [Fact]
    public async Task Handshake_survives_packet_loss()
    {
        var (server, _, net) = await ServerAsync(new DtlsServerOptions().UsePsk(Psk));
        await using (server)
        await using (var client = Client(net, new DtlsClientOptions { Psk = Psk, HandshakeTimeout = TimeSpan.FromSeconds(30) }))
        {
            net.LossRate = 0.2;
            await client.ConnectAsync();
            Assert.Equal("27.5", (await client.GetAsync("/sensors/temperature")).PayloadText);
        }
    }

    [Fact]
    public async Task Lwm2m_registers_over_dtls()
    {
        var net = new InMemoryDatagramNetwork();
        var lwFast = new CoapTransmission { AckTimeout = TimeSpan.FromMilliseconds(300), MaxRetransmit = 4, ResponseTimeout = TimeSpan.FromSeconds(8) };
        var psk = DtlsPsk.FromHex("urn:dev:light:SL60-000417", "000102030405060708090A0B0C0D0E0F");
        await using var server = Lwm2mServer.Create(o =>
        {
            o.UseDtlsServer(() => net.Bind(ServerAddress), new DtlsServerOptions().UsePsk(psk));
            o.Transmission = lwFast;
        });
        await server.StartAsync();
        await using var light = Lwm2mStreetLightSimulator.Create(o =>
        {
            o.UseDtlsClient(() => net.Bind(), ServerAddress, new DtlsClientOptions { Psk = psk });
            o.UseServer(ServerAddress);
            o.Transmission = lwFast;
        });
        await light.StartAsync();
        for (var i = 0; i < 300 && server.Registrations.Count == 0; i++) await Task.Delay(20);
        var r = Assert.Single(server.Registrations);
        Assert.Equal("IoTCom Simulated Lighting", await server.ReadValueAsync(r.Endpoint, Lwm2mPath.Parse("/3/0/0")));
    }

    [Fact]
    public void Options_validate_and_hide_secrets()
    {
        Assert.Throws<ArgumentException>(() => new DtlsClientTransport(new InMemoryDatagramNetwork().Bind(), ServerAddress, new DtlsClientOptions()));
        Assert.DoesNotContain("6A2F", Psk.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, DtlsCertificates.Fingerprint(DtlsCertificates.CreateSelfSigned("x")).Length);
    }
}
