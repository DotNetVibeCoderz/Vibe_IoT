using System.Net;
using System.Text;
using IoTCom.Net.Protocols.LoRaWan;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class LoRaWanCodecTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("lorawan.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Conformance_vectors_decode_verify_and_reencode(string json)
    {
        var v = Conformance.Parse(json);
        var phy = v.Hex("phy");
        var ok = LoRaWanPacket.TryDecode(phy, out var p, out var error);
        Assert.Equal(v.GetProperty("valid").GetBoolean(), ok);
        if (!ok)
        {
            Assert.NotNull(error);
            return;
        }

        switch (v.GetProperty("kind").GetString())
        {
            case "data":
            {
                var keys = new LoRaWanSessionKeys(v.Hex("nwkSKey"), v.Hex("appSKey"));
                var fcnt = v.GetProperty("fcnt").GetUInt32();
                var devAddr = DevAddr.Parse(v.GetProperty("devAddr").GetString()!);
                Assert.Equal((LoRaWanMType)v.GetProperty("mtype").GetInt32(), p!.MType);
                Assert.Equal(devAddr, p.DevAddr);
                Assert.Equal((ushort)fcnt, p.FCnt);
                Assert.Equal(v.Hex("fopts"), p.FOpts.ToArray());
                byte? fport = v.GetProperty("fport").ValueKind == System.Text.Json.JsonValueKind.Null ? null : (byte)v.GetProperty("fport").GetInt32();
                Assert.Equal(fport, p.FPort);
                Assert.True(p.VerifyMic(keys.NwkSKey, fcnt));
                Assert.Equal(v.Hex("payload"), p.DecryptPayload(keys, fcnt));
                var fctrl = new LoRaWanFCtrl((byte)v.GetProperty("fctrl").GetInt32(), p.IsUplink);
                Assert.Equal(phy, LoRaWanPacket.EncodeData(p.MType, devAddr, fctrl, fcnt, v.Hex("fopts"), fport, v.Hex("payload"), keys));
                break;
            }

            case "join-request":
            {
                var appKey = v.Hex("appKey");
                Assert.Equal(Eui64.Parse(v.GetProperty("devEui").GetString()!), p!.DevEui);
                Assert.Equal(Eui64.Parse(v.GetProperty("joinEui").GetString()!), p.JoinEui);
                Assert.Equal(v.GetProperty("devNonce").GetUInt16(), p.DevNonce);
                Assert.True(p.VerifyMic(appKey));
                Assert.Equal(phy, LoRaWanPacket.EncodeJoinRequest(p.JoinEui, p.DevEui, p.DevNonce, appKey));
                break;
            }

            case "join-accept":
            {
                var appKey = v.Hex("appKey");
                Assert.True(LoRaWanJoinAccept.TryDecrypt(phy, appKey, out var accept, out _));
                Assert.Equal(v.GetProperty("joinNonce").GetUInt32(), accept!.JoinNonce);
                Assert.Equal(v.GetProperty("netId").GetUInt32(), accept.NetId);
                Assert.Equal(DevAddr.Parse(v.GetProperty("devAddr").GetString()!), accept.DevAddr);
                Assert.Equal(v.GetProperty("rxDelay").GetByte(), accept.RxDelay);
                var dl = v.GetProperty("dlSettings").GetInt32();
                Assert.Equal(dl >> 4, accept.Rx1DrOffset);
                Assert.Equal(dl & 0x0F, accept.Rx2DataRate);
                var cf = v.Hex("cfList");
                Assert.Equal(cf.Length == 0 ? null : cf, accept.CfList);
                var keys = accept.DeriveSessionKeys(appKey, v.GetProperty("devNonce").GetUInt16());
                Assert.Equal(v.Hex("nwkSKey"), keys.NwkSKey);
                Assert.Equal(v.Hex("appSKey"), keys.AppSKey);
                Assert.Equal(phy, accept.Encode(appKey));
                Assert.False(LoRaWanJoinAccept.TryDecrypt(phy, new byte[16], out _, out var wrongKey));
                Assert.Contains("MIC", wrongKey, StringComparison.Ordinal);
                break;
            }
        }

        var lane = LoRaWanAnatomy.Describe(phy);
        Assert.Equal(phy.Length, lane.Sum(f => f.Length));
        Assert.Equal("MIC", lane[^1].Name);
    }

    [Theory]
    [InlineData("", "BB1D6929E95937287FA37D129B756746")]
    [InlineData("6BC1BEE22E409F96E93D7E117393172A", "070A16B46B4D4144F79BDD9DD04A287C")]
    [InlineData("6BC1BEE22E409F96E93D7E117393172AAE2D8A571E03AC9C9EB76FAC45AF8E5130C81C46A35CE411", "DFA66747DE9AE63030CA32611497C827")]
    public void Aes_cmac_matches_rfc4493(string message, string tag) =>
        Assert.Equal(tag, Convert.ToHexString(LoRaWanCrypto.AesCmac(Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C"), Convert.FromHexString(message))));

    [Fact]
    public void Lora_packet_readme_frame_decrypts_to_test()
    {
        var p = LoRaWanPacket.Decode(Convert.FromHexString("40F17DBE4900020001954378762B11FF0D"));
        var keys = LoRaWanSessionKeys.FromHex("44024241ED4CE9A68C6A8BC055233FD3", "EC925802AE430CA77FD3DD73CB2CC588");
        Assert.Equal("49BE7DF1", p.DevAddr.ToString());
        Assert.True(p.VerifyMic(keys.NwkSKey));
        Assert.Equal("test", Encoding.ASCII.GetString(p.DecryptPayload(keys)));
        Assert.Equal("Unconfirmed up DevAddr=49BE7DF1 FCnt=2 FPort=1 4 B", p.ToString());
    }

    [Theory]
    [InlineData(0xFFFFu, (ushort)0x0000, 0x10000u)]
    [InlineData(0x1FFF0u, (ushort)0xFFF5, 0x1FFF5u)]
    [InlineData(5u, (ushort)9, 9u)]
    [InlineData(9u, (ushort)9, 0x10009u)]
    public void Frame_counter_is_reconstructed_across_16_bit_rollover(uint last, ushort received, uint expected) =>
        Assert.Equal(expected, LoRaWanPacket.ReconstructFCnt(last, received));

    [Theory]
    [InlineData(23, 7, 125, 61.7)]
    [InlineData(23, 12, 125, 1482.8)]
    [InlineData(13, 9, 125, 164.9)]
    [InlineData(51, 10, 125, 616.4)]
    public void Airtime_matches_the_semtech_calculator(int bytes, int sf, int bw, double ms) =>
        Assert.Equal(ms, LoRaAirtime.Compute(bytes, sf, bw).TotalMilliseconds, 1);

    [Fact]
    public void Mac_commands_parse_name_and_describe()
    {
        var down = LoRaWanMacCommands.Parse([0x02, 0x0C, 0x02, 0x06, 0x03, 0x50, 0x07, 0x00, 0x01], uplink: false);
        Assert.Equal(["LinkCheckAns", "DevStatusReq", "LinkADRReq"], down.Select(c => c.Name));
        Assert.Equal("LinkCheckAns(margin 12 dB, 2 gateway(s))", down[0].ToString());
        Assert.Equal("DR5 power 0 mask 0007 NbTrans 1", down[2].Describe());

        var up = LoRaWanMacCommands.Parse(LoRaWanMacCommands.Encode([LoRaWanMacCommand.DevStatusAns(127, -5), LoRaWanMacCommand.LinkCheckReq()]), uplink: true);
        Assert.Equal(["DevStatusAns", "LinkCheckReq"], up.Select(c => c.Name));
        Assert.Equal("battery 50%, margin -5 dB", up[0].Describe());

        var unknown = LoRaWanMacCommands.Parse([0x02, 0x80, 0x01, 0x02], uplink: true);
        Assert.Equal(2, unknown.Count);
        Assert.Equal("CID 0x80", unknown[1].Name);
    }

    [Fact]
    public void Device_time_round_trips_through_gps_epoch()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 30, 15, TimeSpan.Zero);
        Assert.Equal("2026-10-08 12:30:15Z", LoRaWanMacCommand.DeviceTimeAns(now).Describe());
    }

    [Fact]
    public void Regions_map_rx1_and_data_rates()
    {
        Assert.Equal(868.3, LoRaRegion.EU868.Rx1Frequency(868.3));
        Assert.Equal(923.9, LoRaRegion.US915.Rx1Frequency(904.1));
        Assert.Equal(10, LoRaRegion.US915.Rx1DataRate(0));
        Assert.Equal(3, LoRaRegion.Get("eu868").DataRateIndex("SF9BW125"));
        Assert.Same(LoRaRegion.AS923Group2, LoRaRegion.Get("AS923"));
        Assert.Equal(921.4, LoRaRegion.AS923Group2.Rx2Frequency);
    }

    [Fact]
    public void Cayenne_lpp_round_trips()
    {
        var payload = new CayenneLpp().AddTemperature(1, -4.1).AddHumidity(2, 63.5).AddAnalogInput(3, 3.31).AddGps(4, -6.2, 106.8166, 12.5).ToArray();
        Assert.True(CayenneLpp.TryDecode(payload, out var values));
        Assert.Equal(["ch1 temperature -4.1 °C", "ch2 humidity 63.5 %", "ch3 analog in 3.31"], values.Take(3).Select(v => v.ToString()));
        Assert.Equal([-6.2, 106.8166, 12.5], values[3].Values);
        Assert.False(CayenneLpp.TryDecode([0x01, 0xEE, 0x00], out _));
    }
}

public class SemtechUdpTests
{
    [Fact]
    public void Push_data_round_trips_with_rxpk_and_stat()
    {
        var push = new SemtechPacket
        {
            Token = 0xBEEF,
            Type = SemtechPacketType.PushData,
            GatewayEui = Eui64.Parse("AA555A0000000001"),
            RxPackets = [new SemtechRxPacket { Data = [0x40, 1, 2, 3], Tmst = 3512348611, Frequency = 868.1, DataRate = "SF9BW125", Rssi = -97, Snr = 5.5, Channel = 0 }],
            Status = new SemtechGatewayStatus { Time = new DateTimeOffset(2026, 10, 8, 1, 2, 3, TimeSpan.Zero), Latitude = -6.2, Longitude = 106.8, Altitude = 40, RxReceived = 3, RxOk = 2, RxForwarded = 2, AckRatio = 100 },
        };
        var wire = push.Encode();
        Assert.Equal([2, 0xBE, 0xEF, 0x00, 0xAA, 0x55, 0x5A, 0, 0, 0, 0, 1], wire[..12]);
        Assert.True(SemtechPacket.TryDecode(wire, out var back, out _));
        var rx = Assert.Single(back!.RxPackets);
        Assert.Equal(3512348611u, rx.Tmst);
        Assert.Equal("SF9BW125", rx.DataRate);
        Assert.Equal(-97, rx.Rssi);
        Assert.Equal(new byte[] { 0x40, 1, 2, 3 }, rx.Data);
        Assert.Equal(2, back.Status!.RxOk);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 1, 2, 3, TimeSpan.Zero), back.Status.Time);
        Assert.Equal(SemtechPacketType.PushAck, back.Acknowledgement().Type);
        Assert.Equal([2, 0xBE, 0xEF, 0x01], back.Acknowledgement().Encode());
    }

    [Fact]
    public void Pull_resp_round_trips_txpk_and_parses_the_reference_example()
    {
        var resp = new SemtechPacket { Token = 7, Type = SemtechPacketType.PullResp, TxPacket = new SemtechTxPacket { Data = [1, 2, 3], Tmst = 1000, Frequency = 869.525, DataRate = "SF12BW125", Power = 27 } };
        Assert.True(SemtechPacket.TryDecode(resp.Encode(), out var back, out _));
        Assert.Equal(869.525, back!.TxPacket!.Frequency);
        Assert.True(back.TxPacket.InvertPolarity);
        Assert.Equal(27, back.TxPacket.Power);

        // The txpk example from Semtech's PROTOCOL.TXT.
        var json = """{"txpk":{"imme":true,"freq":864.123456,"rfch":0,"powe":14,"modu":"LORA","datr":"SF11BW125","codr":"4/6","ipol":false,"size":32,"data":"H3P3N2i9qc4yt7rK7ldqoeCVJGBybzPY5h1Dd7P7p8v"}}""";
        var wire = new byte[] { 2, 0x12, 0x34, 3 }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        Assert.True(SemtechPacket.TryDecode(wire, out var example, out _));
        Assert.True(example!.TxPacket!.Immediate);
        Assert.Equal("SF11BW125", example.TxPacket.DataRate);
        Assert.Equal(32, example.TxPacket.Data.Length);
        Assert.False(example.TxPacket.InvertPolarity);
    }

    [Theory]
    [InlineData("02")]
    [InlineData("09000000")]
    [InlineData("02000009")]
    [InlineData("02000000AA55")]
    [InlineData("020000037B7B7B")]
    public void Malformed_datagrams_are_rejected_without_throwing(string hex)
    {
        Assert.False(SemtechPacket.TryDecode(Convert.FromHexString(hex), out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(FrameFieldKind.Error, SemtechAnatomy.Describe(Convert.FromHexString(hex))[0].Kind);
    }
}

public class LoRaWanEndDeviceTests
{
    private static readonly byte[] AppKey = LoRaWanKeys.Parse("2B7E151628AED2A6ABF7158809CF4F3C");

    [Fact]
    public void Otaa_join_and_downlink_mac_commands_are_answered_in_the_next_uplink()
    {
        var device = new LoRaWanEndDevice(new Eui64(0x70B3D57ED0000001), default, AppKey) { Battery = 200, LastDownlinkSnr = 7 };
        var join = LoRaWanPacket.Decode(device.CreateJoinRequest());
        Assert.Equal(1, join.DevNonce);
        Assert.True(join.VerifyMic(AppKey));

        var accept = new LoRaWanJoinAccept(0x10, 0x13, new DevAddr(0x26000001), RxDelay: 2);
        Assert.NotNull(device.HandleDownlink(accept.Encode(AppKey)));
        Assert.True(device.IsActivated);
        Assert.Equal(TimeSpan.FromSeconds(2), device.Rx1Delay);
        Assert.Null(device.HandleDownlink(accept.Encode(AppKey)));   // no join pending any more

        var keys = accept.DeriveSessionKeys(AppKey, 1);
        var down = LoRaWanPacket.EncodeData(LoRaWanMType.ConfirmedDataDown, device.DevAddr, LoRaWanFCtrl.Create(false), 0,
            LoRaWanMacCommands.Encode([LoRaWanMacCommand.DevStatusReq(), LoRaWanMacCommand.LinkAdrReq(3, 1, 0x0007)]), 10, [0x00, 0x3C], keys);
        var got = device.HandleDownlink(down)!;
        Assert.True(got.Confirmed);
        Assert.Equal(new byte[] { 0x00, 0x3C }, got.Payload);
        Assert.Equal(3, device.DataRate);
        Assert.Null(device.HandleDownlink(down));   // replayed counter

        var up = LoRaWanPacket.Decode(device.CreateUplink(1, [0xAA]));
        Assert.True(up.FCtrl.Ack);
        Assert.Equal(["DevStatusAns(battery 78%, margin 7 dB)", "LinkADRAns(power ok, DR ok, mask ok)"],
            LoRaWanMacCommands.Parse(up.FOpts.Span, uplink: true).Select(c => c.ToString()));
        Assert.True(up.VerifyMic(keys.NwkSKey, 0));
        Assert.False(LoRaWanPacket.Decode(device.CreateUplink(1, [0xAB])).FCtrl.Ack);
    }
}

public class LoRaWanNetworkTests
{
    private static async Task<(LoRaWanNetworkServer Server, LoRaWanSimulator Sim, InMemoryDatagramNetwork Net)> StartAsync(
        Action<LoRaWanSimulatorOptions>? configure = null, Action<LoRaWanNetworkServerOptions>? server = null)
    {
        var net = new InMemoryDatagramNetwork();
        var serverEp = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1700);
        var ns = LoRaWanNetworkServer.Create(o =>
        {
            o.UseInMemory(net, serverEp);
            o.DeduplicationWindow = TimeSpan.FromMilliseconds(80);
            server?.Invoke(o);
        });
        var options = new LoRaWanSimulatorOptions { Server = serverEp, HonorTimestamps = false, JoinTimeout = TimeSpan.FromSeconds(2), ShadowingDb = 0, Region = LoRaRegion.EU868 };
        options.GatewayTransportFactory = () => net.Bind();
        options.Gateways.Add(new LoRaWanSimulatedGateway(new Eui64(0xAA555A0000000001), "gw-a", 0, 0));
        options.Gateways.Add(new LoRaWanSimulatedGateway(new Eui64(0xAA555A0000000002), "gw-b", 1, 0));
        options.Devices.Add(new() { Name = "env", DevEui = new Eui64(0x70B3D57ED0000101), AppKey = LoRaWanKeys.Random(), X = 0.5, Y = 0.2, Interval = TimeSpan.FromMilliseconds(400), ConfirmedRatio = 1 });
        configure?.Invoke(options);
        var sim = new LoRaWanSimulator(options);
        foreach (var r in sim.Registrations) ns.AddDevice(r);
        await ns.StartAsync();
        await sim.StartAsync();
        return (ns, sim, net);
    }

    private static async Task<T> WaitAsync<T>(TaskCompletionSource<T> tcs, int seconds = 10) =>
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task Devices_join_report_and_get_acks_through_two_gateways()
    {
        var (ns, sim, _) = await StartAsync();
        await using var _ns = ns;
        await using var _sim = sim;
        var joined = new TaskCompletionSource<LoRaWanDeviceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uplink = new TaskCompletionSource<LoRaWanUplink>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ns.DeviceJoined += (_, d) => joined.TrySetResult(d);
        ns.UplinkReceived += (_, u) => uplink.TrySetResult(u);
        sim.RadioActivity += (_, e) => { if (!e.Uplink && e.Summary.StartsWith("ACK", StringComparison.Ordinal)) acked.TrySetResult(true); };

        var device = await WaitAsync(joined);
        Assert.Equal("env", device.Name);
        var up = await WaitAsync(uplink);
        Assert.Equal((byte?)1, up.FPort);
        Assert.True(up.Confirmed);
        Assert.Equal(2, up.Gateways.Count);                     // deduplicated: one event, two receptions
        Assert.True(up.Gateways[0].Snr >= up.Gateways[1].Snr);  // best gateway first
        Assert.True(CayenneLpp.TryDecode(up.Payload, out var values));
        Assert.Equal("temperature", values[0].Name);
        Assert.True(up.Airtime > TimeSpan.Zero);
        Assert.True(await WaitAsync(acked));
        Assert.Equal(2, ns.Gateways.Count);
        Assert.All(ns.Gateways, g => Assert.NotNull(g.PullEndPoint));
    }

    [Fact]
    public async Task Queued_downlinks_reach_the_device_and_change_its_interval()
    {
        var (ns, sim, _) = await StartAsync(o => o.Devices[0] = o.Devices[0] with { Interval = TimeSpan.FromMilliseconds(300), ConfirmedRatio = 0 });
        await using var _ns = ns;
        await using var _sim = sim;
        var joined = new TaskCompletionSource<LoRaWanDeviceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        ns.DeviceJoined += (_, d) => joined.TrySetResult(d);
        var device = await WaitAsync(joined);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sim.RadioActivity += (_, e) => { if (!e.Uplink && e.Summary.Contains("FPort 10", StringComparison.Ordinal)) received.TrySetResult(e.Summary); };
        ns.EnqueueDownlink(device.DevEui, 10, [0x00, 0x2D]);
        ns.EnqueueMacCommand(device.DevEui, LoRaWanMacCommand.DevStatusReq());
        Assert.Contains("set interval 45 s", await WaitAsync(received), StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(45), sim.Devices[0].Interval);
        // The DevStatusAns rides on the next uplink, which the wake-up triggers.
        for (var i = 0; i < 150 && device.Battery is null; i++) await Task.Delay(100);   // slow runners need more than 5 s
        Assert.NotNull(device.Battery);
    }

    [Fact]
    public async Task Server_rejects_unknown_devices_bad_mics_and_replays()
    {
        var net = new InMemoryDatagramNetwork();
        var serverEp = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1700);
        await using var ns = LoRaWanNetworkServer.Create(o => { o.UseInMemory(net, serverEp); o.DeduplicationWindow = TimeSpan.FromMilliseconds(20); });
        var keys = new LoRaWanSessionKeys(LoRaWanKeys.Random(), LoRaWanKeys.Random());
        var abp = LoRaWanEndDevice.Abp(new Eui64(1), new DevAddr(0x26000042), keys);
        ns.AddDevice(LoRaWanDeviceRegistration.Abp(abp.DevEui, abp.DevAddr, keys, "abp"));
        ns.AddDevice(LoRaWanDeviceRegistration.Otaa(new Eui64(2), LoRaWanKeys.Random(), "otaa"));
        await ns.StartAsync();
        await using var gw = SemtechPacketForwarder.Create(o => { o.Server = serverEp; o.UseInMemory(net); o.StatusInterval = TimeSpan.Zero; });
        await gw.ConnectAsync();

        var reasons = new List<string>();
        var uplinks = 0;
        ns.FrameRejected += (_, r) => { lock (reasons) reasons.Add(r); };
        ns.UplinkReceived += (_, _) => Interlocked.Increment(ref uplinks);
        async Task Send(byte[] phy)
        {
            await gw.ForwardAsync(new SemtechRxPacket { Data = phy, Frequency = 868.1, DataRate = "SF7BW125", Snr = 8, Rssi = -60 });
            await Task.Delay(120);
        }

        var first = abp.CreateUplink(1, [1]);
        await Send(first);
        await Send(first);                                                                         // replay
        await Send(LoRaWanPacket.EncodeJoinRequest(default, new Eui64(9), 1, LoRaWanKeys.Random())); // unknown device
        await Send(LoRaWanPacket.EncodeJoinRequest(default, new Eui64(2), 1, LoRaWanKeys.Random())); // wrong AppKey
        var tampered = abp.CreateUplink(1, [2]);
        tampered[^5] ^= 0xFF;
        await Send(tampered);                                                                      // MIC
        await Send(abp.CreateUplink(1, [3]));

        // Slow runners may still be processing the last frames after the 120 ms pauses.
        for (var i = 0; i < 100 && (Volatile.Read(ref uplinks) < 2 || ReasonCount() < 4); i++) await Task.Delay(50);
        int ReasonCount()
        {
            lock (reasons) return reasons.Count;
        }

        Assert.Equal(2, uplinks);
        lock (reasons)
        {
            Assert.Contains(reasons, r => r.Contains("replay", StringComparison.Ordinal));
            Assert.Contains(reasons, r => r.Contains("unknown device", StringComparison.Ordinal));
            Assert.Contains(reasons, r => r.Contains("wrong AppKey", StringComparison.Ordinal));
            Assert.Contains(reasons, r => r.Contains("matching MIC", StringComparison.Ordinal));
        }

        Assert.Equal(2u, ns.GetDevice(new Eui64(1))!.FCntUp);
    }

    [Fact]
    public async Task Real_udp_with_rx1_timing_delivers_the_ack_one_second_after_the_uplink()
    {
        await using var ns = LoRaWanNetworkServer.Create(o =>
        {
            o.UseUdp(0, IPAddress.Loopback);
            o.JoinAcceptDelay = TimeSpan.FromMilliseconds(400);
            o.DeduplicationWindow = TimeSpan.FromMilliseconds(50);
        });
        await ns.StartAsync();
        var options = new LoRaWanSimulatorOptions { Server = ns.LocalEndPoint!, JoinTimeout = TimeSpan.FromSeconds(3), ShadowingDb = 0 };
        options.Gateways.Add(new LoRaWanSimulatedGateway(new Eui64(0xAA555A0000000001), "gw", 0, 0));
        options.Devices.Add(new() { Name = "d", DevEui = new Eui64(0x70B3D57ED0000101), AppKey = LoRaWanKeys.Random(), X = 0.3, Interval = TimeSpan.FromSeconds(30), ConfirmedRatio = 1 });
        await using var sim = new LoRaWanSimulator(options);
        foreach (var r in sim.Registrations) ns.AddDevice(r);
        var events = new List<LoRaWanRadioEvent>();
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sim.RadioActivity += (_, e) =>
        {
            lock (events) events.Add(e);
            if (!e.Uplink && e.Summary.StartsWith("ACK", StringComparison.Ordinal)) acked.TrySetResult();
        };
        await sim.StartAsync();
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (events)
        {
            var join = events.First(e => e.Uplink && e.Summary == "Join-Request");
            var accept = events.First(e => !e.Uplink && e.Summary.StartsWith("Join-Accept", StringComparison.Ordinal));
            var up = events.Last(e => e.Uplink);
            var ack = events.Last(e => !e.Uplink);
            Assert.InRange((accept.Time - join.Time).TotalMilliseconds, 350, 1900);
            Assert.InRange((ack.Time - up.Time).TotalMilliseconds, 950, 2500);   // RX1 is 1 s after the uplink; the upper bound only tolerates busy CI runners
            Assert.Equal(868.1, LoRaRegion.EU868.Rx1Frequency(868.1));
            Assert.Equal(up.Frequency, ack.Frequency);
        }
    }

    [Fact]
    public async Task A_device_out_of_range_loses_its_uplinks()
    {
        var (ns, sim, _) = await StartAsync(o => o.Devices[0] = o.Devices[0] with { X = 40, Y = 0 });
        await using var _ns = ns;
        await using var _sim = sim;
        await Task.Delay(1500);
        Assert.False(ns.Devices.Single().IsActivated);
        Assert.True(sim.Devices[0].Lost > 0);
    }
}
