using System.Security.Cryptography.X509Certificates;
using System.Net;
using IoTCom.Net.Protocols.Matter;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public sealed class MatterCodecTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("matter.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_conformance_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var data = v.Hex("hex");
        var text = System.Text.Encoding.ASCII.GetString(data);
        string got;
        try
        {
            got = v.GetProperty("kind").GetString() switch
            {
                "tlv" => Tlv.Decode(data).ToString(),
                "qr" => MatterSetupPayload.FromQrCode(text).Describe(),
                "manual" => MatterSetupPayload.FromManualCode(text).Describe(),
                "message" => MatterMessage.Decode(data).Describe(),
                "cfid" => Convert.ToHexString(MatterCertificate.CompressedFabricId(data, Convert.ToUInt64(v.GetProperty("fabricId").GetString(), 16))),
                "spake2p" => Spake(v, data),
                var k => throw new ArgumentException(k),
            };
        }
        catch (ProtocolException)
        {
            got = "error";
        }

        Assert.Equal(v.GetProperty("expected").GetString(), got);
    }

    private static string Spake(System.Text.Json.JsonElement v, byte[] context)
    {
        var passcode = v.GetProperty("passcode").GetUInt32();
        var salt = Convert.FromHexString(v.GetProperty("salt").GetString()!);
        var iterations = v.GetProperty("iterations").GetInt32();
        var (w0, w1) = Spake2Plus.ComputeW(passcode, salt, iterations);
        var verifierData = Spake2Plus.ComputeVerifier(passcode, salt, iterations);
        var prover = new Spake2PlusProver(context, w0, w1, Convert.FromHexString(v.GetProperty("x").GetString()!));
        var verifier = new Spake2PlusVerifier(context, verifierData, Convert.FromHexString(v.GetProperty("y").GetString()!));
        var cB = verifier.Respond(prover.X);
        var cA = prover.Finish(verifier.Y, cB);
        Assert.Equal(prover.Ke, verifier.Confirm(cA));
        var (i2r, r2i, challenge) = Spake2Plus.SessionKeys(prover.Ke!);
        static string H(byte[] b) => Convert.ToHexString(b);
        return $"w0={H(w0)} L={H(verifierData[32..])} X={H(prover.X)} Y={H(verifier.Y)} cA={H(cA)} cB={H(cB)} Ke={H(prover.Ke!)} I2R={H(i2r)} R2I={H(r2i)} challenge={H(challenge)}";
    }

    // The CHIP SDK's well-known example device: test vendor 0xFFF1, product 0x8000, BLE, discriminator 3840, passcode 20202021.
    private static readonly MatterSetupPayload Example = new(0xFFF1, 0x8000, CommissioningFlow.Standard, DiscoveryCapabilities.Ble, 3840, 20202021);

    [Fact]
    public void Qr_and_manual_codes_match_the_sdk_example()
    {
        Assert.Equal("MT:Y.K9042C00KA0648G00", Example.ToQrCode());
        Assert.Equal("34970112332", Example.ToManualCode());
        Assert.Equal(Example, MatterSetupPayload.FromQrCode("MT:Y.K9042C00KA0648G00"));
        var manual = MatterSetupPayload.FromManualCode("3497-011-2332");
        Assert.Equal(20202021u, manual.Passcode);
        Assert.Equal(15, manual.ShortDiscriminator);
    }

    [Fact]
    public void Codes_reject_bad_input()
    {
        Assert.Throws<ProtocolException>(() => MatterSetupPayload.FromManualCode("34970112333"));   // check digit
        Assert.Throws<ProtocolException>(() => MatterSetupPayload.FromQrCode("XX:Y.K9042C00KA0648G00"));
        Assert.Throws<ProtocolException>(() => MatterSetupPayload.FromQrCode("MT:Y.K9042C00KA0648G0"));
        Assert.False(MatterSetupPayload.IsValidPasscode(12345678));
        Assert.Throws<ArgumentOutOfRangeException>(() => (Example with { Passcode = 11111111 }).ToQrCode());
        var custom = Example with { Flow = CommissioningFlow.UserIntent };
        var code = custom.ToManualCode();
        Assert.Equal(21, code.Length);
        Assert.Equal(0xFFF1, MatterSetupPayload.FromManualCode(code).VendorId);
    }

    [Fact]
    public void Tlv_round_trips_every_type()
    {
        var e = TlvElement.Structure(TlvTag.Anonymous,
            TlvElement.UInt(TlvTag.Context(0), 42),
            TlvElement.Int(TlvTag.Context(1), -17),
            TlvElement.Bool(TlvTag.Context(2), true),
            TlvElement.String(TlvTag.Context(3), "Lampu"),
            TlvElement.Bytes(TlvTag.Context(4), [0xDE, 0xAD]),
            TlvElement.Null(TlvTag.Context(5)),
            TlvElement.Array(TlvTag.Context(6), TlvElement.UInt(TlvTag.Anonymous, 70000), TlvElement.Double(TlvTag.Anonymous, 1.5)),
            TlvElement.List(TlvTag.Context(7), TlvElement.Float(new TlvTag(TlvTagKind.FullyQualified, 1, 0xFFF1, 0xDEED), 2.5f)));
        var bytes = Tlv.Encode(e);
        Assert.Equal(0x15, bytes[0]);
        Assert.Equal([0x24, 0x00, 0x2A], bytes[1..4]);   // context tag 0, uint8 42
        var back = Tlv.Decode(bytes);
        Assert.Equal("{0=42u,1=-17,2=true,3=\"Lampu\",4=h'DEAD',5=null,6=[70000u,1.5d],7=(FFF1:DEED:1=2.5f)}", back.ToString());
        Assert.Equal(bytes, Tlv.Encode(back));
        Assert.Equal(-17, back.Required(1).AsInt());
        Assert.Throws<ProtocolException>(() => Tlv.Decode([0x15, 0x24, 0x00]));          // truncated
        Assert.Throws<ProtocolException>(() => Tlv.Decode([0x16, 0x24, 0x00, 0x01, 0x18]));   // tagged member in an array
        Assert.Throws<ProtocolException>(() => Tlv.Decode([0x04, 0x01, 0x00]));          // trailing byte
    }

    [Fact]
    public void Spake2_plus_agrees_on_the_right_passcode_only()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var context = Spake2Plus.Context("request"u8.ToArray(), "response"u8.ToArray());
        var verifier = Spake2Plus.ComputeVerifier(20202021, salt, 1000);
        var (w0, w1) = Spake2Plus.ComputeW(20202021, salt, 1000);
        var prover = new Spake2PlusProver(context, w0, w1);
        var device = new Spake2PlusVerifier(context, verifier);
        var cB = device.Respond(prover.X);
        var cA = prover.Finish(device.Y, cB);
        Assert.Equal(prover.Ke, device.Confirm(cA));
        var (i2r, r2i, challenge) = Spake2Plus.SessionKeys(prover.Ke!);
        Assert.Equal(16, i2r.Length);
        Assert.NotEqual(i2r, r2i);
        Assert.Equal(16, challenge.Length);

        var (b0, b1) = Spake2Plus.ComputeW(20202022, salt, 1000);
        var wrong = new Spake2PlusProver(context, b0, b1);
        var device2 = new Spake2PlusVerifier(context, verifier);
        Assert.Throws<ProtocolException>(() => wrong.Finish(device2.Y, device2.Respond(wrong.X)));
    }

    [Fact]
    public void Aes_ccm_and_ecdsa_round_trip()
    {
        var key = MatterCrypto.RandomBytes(16);
        var nonce = MatterCrypto.RandomBytes(13);
        var sealed_ = MatterCrypto.Encrypt(key, nonce, "on"u8.ToArray(), [1, 2, 3]);
        Assert.Equal(2 + 16, sealed_.Length);
        Assert.Equal("on"u8.ToArray(), MatterCrypto.Decrypt(key, nonce, sealed_, [1, 2, 3]));
        sealed_[0] ^= 1;
        Assert.Throws<ProtocolException>(() => MatterCrypto.Decrypt(key, nonce, sealed_, [1, 2, 3]));
        var (priv, pub) = MatterCrypto.GenerateKeyPair();
        Assert.Equal(pub, MatterCrypto.PublicKeyOf(priv));
        var sig = MatterCrypto.Sign(priv, "tbs"u8.ToArray());
        Assert.True(MatterCrypto.Verify(pub, "tbs"u8.ToArray(), sig));
        Assert.False(MatterCrypto.Verify(pub, "tbx"u8.ToArray(), sig));
        var (priv2, pub2) = MatterCrypto.GenerateKeyPair();
        Assert.Equal(MatterCrypto.Ecdh(priv, pub2), MatterCrypto.Ecdh(priv2, pub));
    }

    [Fact]
    public void Operational_certificates_issue_round_trip_and_load_as_x509()
    {
        var (rootKey, rootPub) = MatterCrypto.GenerateKeyPair();
        var root = MatterCertificate.CreateRoot(rootKey, rcacId: 1, fabricId: 0x2906C908D115D362);
        Assert.True(root.VerifySignature(rootPub));
        var (nodeKey, _) = MatterCrypto.GenerateKeyPair();
        var csr = MatterCertificate.CreateCsr(nodeKey);
        var nodePub = MatterCertificate.PublicKeyFromCsr(csr);
        Assert.Equal(MatterCrypto.PublicKeyOf(nodeKey), nodePub);
        var noc = MatterCertificate.CreateNoc(nodePub, nodeId: 0x0000000000000042, fabricId: 0x2906C908D115D362, root, rootKey);
        var back = MatterCertificate.Parse(noc.EncodeTlv());
        Assert.Equal(noc.TbsDer(), back.TbsDer());
        Assert.True(back.VerifySignature(rootPub));
        Assert.False(back.VerifySignature(nodePub));
        Assert.Equal(0x42ul, back.NodeId);

        // The reconstructed DER is a real X.509 certificate: .NET loads it, sees the Matter DN, and its key verifies the root.
        using var x509 = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(back.ToX509Der());
        Assert.Contains("OID.1.3.6.1.4.1.37244.1.1=0000000000000042", x509.Subject);
        using var rootX509 = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(root.ToX509Der());
        Assert.Equal(rootX509.Subject, x509.Issuer);
        using var rootKeyFromX509 = rootX509.GetECDsaPublicKey()!;
        Assert.True(rootKeyFromX509.VerifyData(back.TbsDer(), back.Signature, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(8, MatterCertificate.CompressedFabricId(rootPub, 0x2906C908D115D362).Length);
        Assert.Throws<ProtocolException>(() => MatterCertificate.PublicKeyFromCsr([1, 2, 3]));
    }
}

public sealed class MatterCommissioningTests
{
    private static readonly IPEndPoint LightAt = new(IPAddress.Parse("10.0.0.70"), 5540);

    private static async Task<(MatterDevice Device, MatterClusters.OnOffCluster OnOff, MatterClusters.LevelControlCluster Level, InMemoryDatagramNetwork Net)> LightAsync()
    {
        var net = new InMemoryDatagramNetwork(seed: 3);
        var device = MatterDevice.Create(o =>
        {
            o.UseInMemory(net, LightAt);
            o.ProductName = "Lampu teras";
            o.Reliability.Interval = TimeSpan.FromMilliseconds(100);
        });
        var ep = device.AddEndpoint(new MatterEndpoint(1, (MatterIds.DimmableLight, 3)));
        var onOff = ep.Add(new MatterClusters.OnOffCluster());
        var level = ep.Add(new MatterClusters.LevelControlCluster(128, onOff));
        await device.StartAsync();
        return (device, onOff, level, net);
    }

    private static MatterController Controller(InMemoryDatagramNetwork net, bool writes = true, bool trust = true) => MatterController.Create(o =>
    {
        o.UseInMemory(net);
        o.Reliability.Interval = TimeSpan.FromMilliseconds(100);
        if (writes) o.AllowWrites();
        if (trust) o.TrustTestDevices();
    });

    [Fact]
    public async Task Commissions_reads_invokes_writes_and_subscribes()
    {
        var (device, onOff, level, net) = await LightAsync();
        await using var _d = device;
        await using var controller = Controller(net);
        MatterFabricInfo? commissioned = null;
        device.Commissioned += f => commissioned = f;
        var node = await controller.CommissionAsync(LightAt, device.SetupPayload, nodeId: 0x42);
        Assert.NotNull(commissioned);
        Assert.Equal(controller.FabricId, commissioned!.FabricId);
        Assert.Equal(0x42ul, commissioned.NodeId);
        Assert.False(device.CommissioningWindowOpen);

        Assert.Equal("Lampu teras", (await node.ReadAttributeAsync(0, MatterIds.BasicInformation, 3)).AsString());
        Assert.False((await node.ReadAttributeAsync(1, MatterIds.OnOff, 0)).AsBool());
        var parts = await node.ReadAttributeAsync(0, MatterIds.Descriptor, 3);
        Assert.Equal([1ul], parts.Children.Select(c => c.AsUInt()));
        var all = await node.ReadAsync([new AttributePath(1, null, null)]);
        Assert.Contains(all, r => r.Path.Cluster == MatterIds.LevelControl && r.Path.Attribute == 0 && r.Value!.AsUInt() == 128);

        var subscribed = new List<AttributeReport>();
        var sub = await node.SubscribeAsync([new AttributePath(1, MatterIds.OnOff, 0)], 0, 10, r => { lock (subscribed) subscribed.Add(r); });
        Assert.True(sub.Id > 0);
        Assert.Single(subscribed);   // priming report

        Assert.Equal(MatterStatus.Success, (await node.InvokeAsync(1, MatterIds.OnOff, 2)).Status);   // Toggle
        Assert.True(onOff.On);
        var (_, levelStatus) = await node.InvokeAsync(1, MatterIds.LevelControl, 4, TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), 200),
            TlvElement.Null(TlvTag.Context(1)), TlvElement.UInt(TlvTag.Context(2), 0), TlvElement.UInt(TlvTag.Context(3), 0)));
        Assert.Equal(MatterStatus.Success, levelStatus);
        Assert.Equal(200, level.Level);
        Assert.Equal(MatterStatus.ConstraintError, (await node.InvokeAsync(1, MatterIds.LevelControl, 0, TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), 255)))).Status);
        Assert.Equal(MatterStatus.UnsupportedCommand, (await node.InvokeAsync(1, MatterIds.OnOff, 0x40)).Status);

        Assert.Equal(MatterStatus.Success, await node.WriteAttributeAsync(0, MatterIds.BasicInformation, 5, TlvElement.String(TlvTag.Anonymous, "Teras depan")));
        Assert.Equal("Teras depan", device.NodeLabel);
        Assert.Equal(MatterStatus.UnsupportedWrite, await node.WriteAttributeAsync(0, MatterIds.BasicInformation, 3, TlvElement.String(TlvTag.Anonymous, "x")));

        for (var i = 0; i < 100 && !subscribed.Any(r => r.Value?.AsBool() == true); i++) await Task.Delay(20);
        Assert.Contains(subscribed, r => r.Value?.AsBool() == true);
        Assert.Equal(1, device.SubscriptionCount);

        var fabrics = await node.ReadAttributeAsync(0, MatterIds.OperationalCredentials, 1);
        Assert.Equal(controller.FabricId, Assert.Single(fabrics.Children).Required(3).AsUInt());
    }

    [Fact]
    public async Task Wrong_passcode_untrusted_device_and_read_only_are_refused()
    {
        var (device, _, _, net) = await LightAsync();
        await using var _d = device;
        await using (var readOnly = Controller(net, writes: false))
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => readOnly.CommissionAsync(LightAt, device.SetupPayload, 1));
        await using (var wrong = Controller(net))
            Assert.Contains("passcode", (await Assert.ThrowsAsync<MatterException>(() => wrong.CommissionAsync(LightAt, 12121212, 1))).Message);
        await using (var untrusting = Controller(net, trust: false))
            Assert.Contains("attestation", (await Assert.ThrowsAsync<MatterException>(() => untrusting.CommissionAsync(LightAt, device.SetupPayload, 1))).Message);
        Assert.Empty(device.Fabrics);   // the fail-safe was not completed... the NOC was never added
    }

    [Fact]
    public async Task Stranger_fabrics_cannot_open_case_and_reconnect_works()
    {
        var (device, onOff, _, net) = await LightAsync();
        await using var _d = device;
        await using var owner = Controller(net);
        await owner.CommissionAsync(LightAt, device.SetupPayload, nodeId: 7);
        await using var stranger = Controller(net);
        await stranger.ConnectAsync();
        await Assert.ThrowsAsync<MatterException>(() => stranger.ConnectAsync(LightAt, 7));
        await Assert.ThrowsAsync<MatterException>(() => stranger.CommissionAsync(LightAt, device.SetupPayload, 9));   // window closed
        var again = await owner.ConnectAsync(LightAt, 7);
        await again.InvokeAsync(1, MatterIds.OnOff, 1);
        Assert.True(onOff.On);
    }

    [Fact]
    public async Task Fabric_file_survives_a_restart()
    {
        var (device, onOff, _, net) = await LightAsync();
        await using var _d = device;
        var file = Path.Combine(Path.GetTempPath(), $"iotcom-matter-{Guid.NewGuid():N}.json");
        try
        {
            await using (var first = MatterController.Create(o => { o.UseInMemory(net); o.AllowWrites().TrustTestDevices(); o.FabricFile = file; }))
                await first.CommissionAsync(LightAt, device.SetupPayload, nodeId: 3);
            Assert.Contains("operationalKey", File.ReadAllText(file));
            await using var second = MatterController.Create(o => { o.UseInMemory(net); o.AllowWrites(); o.FabricFile = file; });
            var node = await second.ConnectAsync(LightAt, 3);
            await node.InvokeAsync(1, MatterIds.OnOff, 1);
            Assert.True(onOff.On);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Devices_are_found_over_dns_sd_before_and_after_commissioning()
    {
        var net = new InMemoryDatagramNetwork(seed: 9);
        var at = new IPEndPoint(IPAddress.Parse("10.0.0.72"), 5540);
        await using var responder = MdnsResponder.Create(o => o.UseInMemory(net, at.Address));
        await responder.StartAsync();
        var device = MatterDevice.Create(o => { o.UseInMemory(net, at); o.Mdns = responder; o.Discriminator = 1234; o.Reliability.Interval = TimeSpan.FromMilliseconds(100); });
        device.AddEndpoint(new MatterEndpoint(1, (MatterIds.OnOffLight, 3))).Add(new MatterClusters.OnOffCluster());
        await device.StartAsync();
        await using var _d = device;
        await using var browser = MdnsBrowser.Create(o => o.UseInMemory(net, IPAddress.Parse("10.0.0.90")));
        await browser.StartAsync();
        var found = Assert.Single(await MatterDiscovery.FindCommissionableAsync(browser, TimeSpan.FromMilliseconds(400), discriminator: 1234));
        Assert.Equal(at, found.Address);
        Assert.Equal(0xFFF1, found.VendorId);

        await using var controller = Controller(net);
        await controller.CommissionAsync(found.Address, device.SetupPayload, nodeId: 0x99);
        await Task.Delay(100);
        Assert.Equal(at, await MatterDiscovery.FindOperationalAsync(browser, controller, 0x99, TimeSpan.FromMilliseconds(400)));
        Assert.Empty(await MatterDiscovery.FindCommissionableAsync(browser, TimeSpan.FromMilliseconds(400), discriminator: 1234));
    }

    [Fact]
    public async Task Bridge_exposes_bridged_devices()
    {
        var net = new InMemoryDatagramNetwork(seed: 4);
        var at = new IPEndPoint(IPAddress.Parse("10.0.0.71"), 5540);
        var device = MatterDevice.Create(o => { o.UseInMemory(net, at); o.ProductName = "IoTCom smart home bridge"; o.Reliability.Interval = TimeSpan.FromMilliseconds(100); });
        var sim = new MatterBridgeSimulator(device);
        await device.StartAsync();
        await using var _d = device;
        await using var controller = Controller(net);
        var node = await controller.CommissionAsync(at, device.SetupPayload, nodeId: 0x55);

        var aggregatorParts = await node.ReadAttributeAsync(1, MatterIds.Descriptor, 3);
        Assert.Equal([2ul, 3, 4, 5, 6, 7], aggregatorParts.Children.Select(c => c.AsUInt()));
        var types = await node.ReadAttributeAsync(2, MatterIds.Descriptor, 0);
        Assert.Contains(types.Children, t => t.Required(0).AsUInt() == MatterIds.BridgedNode);
        Assert.Equal("Kitchen", (await node.ReadAttributeAsync(3, MatterIds.BridgedDeviceBasicInformation, 5)).AsString());
        Assert.Equal(2350, (await node.ReadAttributeAsync(6, MatterIds.TemperatureMeasurement, 0)).AsInt());

        await node.InvokeAsync(4, MatterIds.OnOff, 1);
        Assert.True(sim.Lights[2].OnOff.On);   // terrace
        var seen = new List<AttributeReport>();
        await node.SubscribeAsync([new AttributePath(null, MatterIds.BridgedDeviceBasicInformation, 0x11)], 0, 30, r => { lock (seen) seen.Add(r); });
        sim.SetReachable(5, false);
        for (var i = 0; i < 100 && !seen.Any(r => r.Path.Endpoint == 5 && r.Value?.AsBool() == false); i++) await Task.Delay(20);
        Assert.Contains(seen, r => r.Path.Endpoint == 5 && r.Value?.AsBool() == false);
    }
}
