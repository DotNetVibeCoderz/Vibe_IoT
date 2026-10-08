using IoTCom.Net.Adapters.OpcUa;

namespace IoTCom.Net.Tests.Protocols;

/// <summary>One simulator per test class: certificate creation and server start take a moment.</summary>
public sealed class OpcUaFixture : IAsyncLifetime
{
    public OpcUaPlantServer Server { get; private set; } = null!;

    public string Pki { get; } = Path.Combine(Path.GetTempPath(), "iotcom-opcua-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Server = OpcUaPlantServer.Create(o =>
        {
            o.Port = SparkplugPorts.Free();
            o.Interval = TimeSpan.FromMilliseconds(200);
            o.PkiPath = Path.Combine(Pki, "server");
        });
        await Server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        try
        {
            Directory.Delete(Pki, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class OpcUaTests(OpcUaFixture fixture) : IClassFixture<OpcUaFixture>
{
    private OpcUaClient Client(bool secure = false, bool readOnly = false) => OpcUaClient.Create(o =>
    {
        o.UseEndpoint(fixture.Server.EndpointUrl);
        o.UseSecurity = secure;
        o.AcceptUntrustedCertificates = true;
        o.ReadOnly = readOnly;
        o.PkiPath = Path.Combine(fixture.Pki, "client");
    });

    [Fact]
    public async Task Browses_reads_and_writes_the_plant()
    {
        await using var ua = Client();
        await ua.ConnectAsync();
        Assert.Equal("None", ua.SecurityPolicy);
        Assert.Contains(OpcUaPlantServer.NamespaceUri, ua.Namespaces);

        var top = await ua.BrowseAsync();
        var plant = Assert.Single(top, n => n.DisplayName == "Plant");
        var line = Assert.Single(await ua.BrowseAsync(plant.NodeId), n => n.DisplayName == "Line1");
        var children = await ua.BrowseAsync(line.NodeId);
        Assert.Contains(children, n => n.DisplayName == "Filler" && n.IsContainer);
        Assert.Contains(children, n => n.DisplayName == "ResetCounter" && n.NodeClass == "Method");

        var setpoint = fixture.Server.NodeId("Line1/Filler/Setpoint");
        await ua.WriteAsync(setpoint, "100");                        // converted to Double
        Assert.Equal(100.0, (await ua.ReadAsync(setpoint)).Value);
        await ua.WriteAsync(setpoint, 120.0);

        var speed = await ua.ReadAsync(fixture.Server.NodeId("Line1/Filler/Speed"));
        Assert.True(speed.IsGood);
        Assert.IsType<double>(speed.Value);
        Assert.NotNull(speed.SourceTimestamp);

        var missing = await ua.ReadAsync("ns=2;s=Plant/Nope");
        Assert.False(missing.IsGood);
        Assert.Equal("BadNodeIdUnknown", missing.Status);
        await Assert.ThrowsAsync<DeviceException>(() => ua.WriteAsync(fixture.Server.NodeId("Line1/Filler/Speed"), 5.0));   // not writable
    }

    [Fact]
    public async Task Methods_and_read_only_mode()
    {
        await using var ua = Client();
        await ua.ConnectAsync();
        var outputs = await ua.CallAsync(fixture.Server.NodeId("Line1"), fixture.Server.NodeId("Line1/ResetCounter"));
        Assert.IsType<long>(Assert.Single(outputs));

        await using var ro = Client(readOnly: true);
        await ro.ConnectAsync();
        Assert.True((await ro.ReadAsync(fixture.Server.NodeId("Line1/Filler/Running"))).IsGood);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => ro.WriteAsync(fixture.Server.NodeId("Line1/Filler/Running"), false));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => ro.CallAsync(fixture.Server.NodeId("Line1"), fixture.Server.NodeId("Line1/ResetCounter")));
    }

    [Fact]
    public async Task Subscriptions_deliver_changes()
    {
        await using var ua = Client();
        await ua.ConnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var seen = new List<OpcUaValue>();
        await foreach (var v in ua.SubscribeAsync([fixture.Server.NodeId("Line1/Tank7/Temperature"), fixture.Server.NodeId("Line1/Filler/BottleCount")], TimeSpan.FromMilliseconds(100), cts.Token))
        {
            seen.Add(v);
            if (seen.Count(x => x.NodeId.EndsWith("Temperature", StringComparison.Ordinal)) >= 3) break;
        }

        Assert.Contains(seen, v => v.NodeId.EndsWith("BottleCount", StringComparison.Ordinal));
        Assert.True(seen.Where(v => v.NodeId.EndsWith("Temperature", StringComparison.Ordinal)).Select(v => v.Value).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task Secure_session_with_basic256sha256()
    {
        await using var ua = Client(secure: true);
        await ua.ConnectAsync();
        Assert.Equal("Basic256Sha256", ua.SecurityPolicy);
        Assert.Equal("SignAndEncrypt", ua.SecurityMode);
        Assert.True((await ua.ReadAsync(fixture.Server.NodeId("Location"))).IsGood);
    }

    [Fact]
    public async Task Unreachable_server_is_a_transport_error()
    {
        await using var ua = OpcUaClient.Create(o => { o.UseEndpoint($"opc.tcp://127.0.0.1:{SparkplugPorts.Free()}"); o.UseSecurity = false; o.PkiPath = Path.Combine(fixture.Pki, "client"); });
        await Assert.ThrowsAsync<TransportException>(async () => await ua.ConnectAsync());
    }
}
