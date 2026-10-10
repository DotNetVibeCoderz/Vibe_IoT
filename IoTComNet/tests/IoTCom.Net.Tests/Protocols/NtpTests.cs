using System.Net;
using IoTCom.Net.Protocols.Ntp;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class NtpCodecTests
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Timestamps_convert_and_cross_the_2036_era()
    {
        Assert.Equal(0x83AA7E80_00000000UL, NtpTimestamp.FromDateTime(UnixEpoch).Raw);                    // 2 208 988 800 s
        Assert.Equal(0x83AA7E80_80000000UL, NtpTimestamp.FromDateTime(UnixEpoch.AddMilliseconds(500)).Raw);
        Assert.Equal(UnixEpoch, new NtpTimestamp(0x83AA7E80_00000000UL).ToDateTime());

        var t = new DateTime(2026, 10, 9, 6, 30, 15, 250, DateTimeKind.Utc).AddTicks(1234);
        Assert.Equal(t, NtpTimestamp.FromDateTime(t).ToDateTime());

        var era1 = new DateTime(2036, 2, 7, 6, 28, 16, DateTimeKind.Utc);
        Assert.Equal(0UL, NtpTimestamp.FromDateTime(era1).Raw);
        Assert.Equal(era1.AddSeconds(1), new NtpTimestamp(1UL << 32).ToDateTime());
        Assert.Equal(new DateTime(2050, 1, 1, 0, 0, 0, DateTimeKind.Utc), NtpTimestamp.FromDateTime(new DateTime(2050, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToDateTime());
        // On-wire difference across the boundary: one second before and after.
        Assert.Equal(2.0, NtpTimestamp.Difference(NtpTimestamp.FromDateTime(era1.AddSeconds(1)), NtpTimestamp.FromDateTime(era1.AddSeconds(-1))), 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => NtpTimestamp.FromDateTime(new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NtpTimestamp.FromDateTime(new DateTime(2110, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Packets_round_trip()
    {
        var request = new NtpPacket { Mode = NtpMode.Client, Transmit = NtpTimestamp.FromDateTime(UnixEpoch) }.Encode();
        Assert.Equal(48, request.Length);
        Assert.Equal(0x23, request[0]);   // LI 0, VN 4, mode 3
        Assert.Equal("83AA7E8000000000", Convert.ToHexString(request, 40, 8));

        var reply = new NtpPacket
        {
            Mode = NtpMode.Server, Stratum = 1, Poll = 6, Precision = -23, RootDelay = 0.0153, RootDispersion = 0.25,
            ReferenceId = NtpPacket.ReferenceCode("GPS"), Transmit = new NtpTimestamp(0xE9A1B2C3_40000000), Trailer = new byte[] { 1, 2, 3, 4 },
        };
        var bytes = reply.Encode();
        Assert.Equal("24 01 06 E9 000003EB 00004000 47505300".Replace(" ", ""), Convert.ToHexString(bytes, 0, 16));
        var back = NtpPacket.Parse(bytes);
        Assert.Equal("GPS", back.ReferenceIdText);
        Assert.Equal(0.0153, back.RootDelay, 4);
        Assert.Equal(0.25, back.RootDispersion);
        Assert.Equal(reply.Transmit, back.Transmit);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, back.Trailer.ToArray());
        Assert.Equal(bytes, back.Encode());

        var relay = new NtpPacket { Mode = NtpMode.Server, Stratum = 2, ReferenceId = NtpPacket.ReferenceAddress(IPAddress.Parse("192.0.2.17")) };
        Assert.Equal("192.0.2.17", NtpPacket.Parse(relay.Encode()).ReferenceIdText);
        var kod = NtpPacket.Parse(new NtpPacket { Leap = NtpLeap.Unsynchronised, Mode = NtpMode.Server, Stratum = 0, ReferenceId = NtpPacket.ReferenceCode("RATE") }.Encode());
        Assert.True(kod.IsKissOfDeath);
        Assert.Equal("RATE", kod.ReferenceIdText);
        Assert.Throws<ProtocolException>(() => NtpPacket.Parse(new byte[47]));
        Assert.Contains(NtpPacket.Describe(kod.Encode()), f => f.Name == "Stratum" && f.Kind == FrameFieldKind.Error);
    }

    [Fact]
    public void Offset_and_delay_follow_rfc_5905()
    {
        // Server 100 ms ahead, 50 ms each way, 1 ms in the server.
        var t0 = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        NtpTimestamp T(double s) => NtpTimestamp.FromDateTime(t0.AddSeconds(s));
        var (t1, t2, t3, t4) = (T(10.000), T(10.150), T(10.151), T(10.101));
        Assert.Equal(100, NtpMath.Offset(t1, t2, t3, t4).TotalMilliseconds, 3);
        Assert.Equal(100, NtpMath.Delay(t1, t2, t3, t4).TotalMilliseconds, 3);
    }
}

public class NtpSessionTests
{
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.1"), 123);

    private static async Task<NtpServer> ServerAsync(InMemoryDatagramNetwork net, IPEndPoint at, Action<NtpServerOptions>? configure = null)
    {
        var server = NtpServer.Create(o =>
        {
            o.UseInMemory(net, at);
            configure?.Invoke(o);
        });
        await server.StartAsync();
        return server;
    }

    private static SntpClient Client(InMemoryDatagramNetwork net, Func<DateTime> clock, params IPEndPoint[] servers) => SntpClient.Create(o =>
    {
        o.UseInMemory(net);
        foreach (var s in servers) o.UseServer(s);
        o.Clock = clock;
        o.MinimumPollInterval = TimeSpan.Zero;
        o.Timeout = TimeSpan.FromMilliseconds(500);
    });

    [Fact]
    public async Task A_drifting_clock_is_measured_and_corrected()
    {
        var net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(20) };
        await using var server = await ServerAsync(net, ServerAddress);
        var device = new DriftingClock(TimeSpan.FromSeconds(-2.5), driftPpm: 40);
        await using var client = Client(net, () => device.UtcNow, ServerAddress);

        var r = await client.QueryAsync(ServerAddress);
        // NTP's offset error is at most half the round trip (an asymmetric path), plus a little for the clock reads.
        var bound = (r.RoundTripDelay.TotalSeconds / 2) + 0.02;
        Assert.InRange(r.RoundTripDelay.TotalMilliseconds, 30, 2000);
        Assert.InRange(r.Offset.TotalSeconds, 2.5 - bound, 2.5 + bound);
        Assert.Equal(1, r.Stratum);
        device.Step(r.Offset);
        Assert.InRange(Math.Abs(device.Error.TotalSeconds), 0, bound + 0.01);
        Assert.Equal(1, server.Answered);
    }

    [Fact]
    public async Task Rate_limits_unsynchronised_servers_and_timeouts()
    {
        var net = new InMemoryDatagramNetwork();
        await using var server = await ServerAsync(net, ServerAddress, o => o.RateLimit = TimeSpan.FromSeconds(5));
        await using var client = Client(net, () => DateTime.UtcNow, ServerAddress);
        await client.QueryAsync(ServerAddress);
        var kod = await Assert.ThrowsAsync<NtpKissOfDeathException>(() => client.QueryAsync(ServerAddress));
        Assert.Equal("RATE", kod.Code);
        Assert.Equal(1, server.KissesSent);

        var lost = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 123);
        await using var unsynced = await ServerAsync(net, lost, o => o.Leap = NtpLeap.Unsynchronised);
        await Assert.ThrowsAsync<DeviceException>(() => client.QueryAsync(lost));
        await Assert.ThrowsAsync<IoTComTimeoutException>(() => client.QueryAsync(new IPEndPoint(IPAddress.Parse("10.0.0.99"), 123)));
    }

    [Fact]
    public async Task Forged_answers_are_ignored_and_other_modes_get_no_answer()
    {
        var net = new InMemoryDatagramNetwork();
        await using var fake = net.Bind(ServerAddress);
        await using var client = Client(net, () => DateTime.UtcNow, ServerAddress);
        var query = client.QueryAsync(ServerAddress);
        var request = await fake.ReceiveAsync();
        var asked = NtpPacket.Parse(request.Data.Span);
        var now = NtpTimestamp.FromDateTime(DateTime.UtcNow.AddSeconds(30));
        // First an answer with a guessed originate, then the genuine one.
        await fake.SendAsync(new NtpPacket { Mode = NtpMode.Server, Stratum = 2, Originate = NtpTimestamp.FromDateTime(DateTime.UtcNow), Receive = now, Transmit = now }.Encode(), request.Remote);
        var real = NtpTimestamp.FromDateTime(DateTime.UtcNow);
        await fake.SendAsync(new NtpPacket { Mode = NtpMode.Server, Stratum = 2, Originate = asked.Transmit, Receive = real, Transmit = real }.Encode(), request.Remote);
        var r = await query;
        Assert.InRange(Math.Abs(r.Offset.TotalSeconds), 0, 1);

        await using var server = await ServerAsync(net, new IPEndPoint(IPAddress.Parse("10.0.0.3"), 123));
        await using var raw = net.Bind();
        await raw.SendAsync(new NtpPacket { Mode = NtpMode.SymmetricActive, Transmit = real }.Encode(), new IPEndPoint(IPAddress.Parse("10.0.0.3"), 123));
        await raw.SendAsync(new NtpPacket { Version = 3, Mode = NtpMode.Client, Transmit = real }.Encode(), new IPEndPoint(IPAddress.Parse("10.0.0.3"), 123));
        var answer = NtpPacket.Parse((await raw.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Data.Span);
        Assert.Equal(3, answer.Version);                   // version echoed
        Assert.Equal(real, answer.Originate);              // only the client-mode request was answered
        Assert.Equal(1, server.Answered);
    }

    [Fact]
    public async Task Several_servers_outvote_a_falseticker()
    {
        var net = new InMemoryDatagramNetwork { Latency = TimeSpan.FromMilliseconds(5) };
        var addresses = Enumerable.Range(1, 3).Select(i => new IPEndPoint(IPAddress.Parse($"10.0.1.{i}"), 123)).ToArray();
        await using var a = await ServerAsync(net, addresses[0]);
        await using var b = await ServerAsync(net, addresses[1]);
        await using var liar = await ServerAsync(net, addresses[2], o => o.Clock = () => DateTime.UtcNow.AddSeconds(30));
        var silent = new IPEndPoint(IPAddress.Parse("10.0.1.9"), 123);
        await using var client = Client(net, () => DateTime.UtcNow, [.. addresses, silent]);

        var estimate = await client.SynchronizeAsync();
        Assert.Equal(3, estimate.Accepted.Count);
        Assert.InRange(Math.Abs(estimate.Offset.TotalMilliseconds), 0, 50);    // the median ignores the +30 s server
        Assert.Single(estimate.Failed);
    }
}

public class NtpConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("ntp.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var expected = v.GetProperty("fields").GetString();
        switch (v.GetProperty("kind").GetString())
        {
            case "packet":
                var data = v.Hex("data");
                if (expected == "error")
                {
                    Assert.Throws<ProtocolException>(() => NtpPacket.Parse(data));
                    return;
                }

                var p = NtpPacket.Parse(data);
                var rendered = $"{(byte)p.Leap}|{p.Version}|{(byte)p.Mode}|{p.Stratum}|{p.Poll}|{p.Precision}|{(uint)Math.Round(p.RootDelay * 65536)}|{(uint)Math.Round(p.RootDispersion * 65536)}|{p.ReferenceIdText}|{p.Reference.Raw:X16}|{p.Originate.Raw:X16}|{p.Receive.Raw:X16}|{p.Transmit.Raw:X16}|{p.Trailer.Length}";
                Assert.Equal(expected, rendered);
                Assert.Equal(data, p.Encode());
                break;
            case "timestamp":
                var ts = new NtpTimestamp(Convert.ToUInt64(v.GetProperty("raw").GetString(), 16));
                Assert.Equal(expected, ts.ToDateTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case "exchange":
                NtpTimestamp R(string name) => new(Convert.ToUInt64(v.GetProperty(name).GetString(), 16));
                var parts = expected!.Split('|');
                // Offset and delay in 100 ns ticks (TimeSpan resolution), within one tick of the reference.
                var offset = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                var delay = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(NtpMath.Offset(R("t1"), R("t2"), R("t3"), R("t4")).Ticks, offset - 1, offset + 1);
                Assert.InRange(NtpMath.Delay(R("t1"), R("t2"), R("t3"), R("t4")).Ticks, delay - 1, delay + 1);
                break;
        }
    }
}
