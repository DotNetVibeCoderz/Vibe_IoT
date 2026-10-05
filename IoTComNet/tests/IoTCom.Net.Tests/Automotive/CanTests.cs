using IoTCom.Net.Transport.Can;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Automotive;

public sealed class CanFrameTests
{
    [Theory]
    [InlineData("123#DEADBEEF", 0x123u, false, 4)]
    [InlineData("18DAF110#0322F190", 0x18DAF110u, true, 4)]
    [InlineData("7DF#", 0x7DFu, false, 0)]
    [InlineData("123#R", 0x123u, false, 0)]
    public void Candump_notation_round_trips(string text, uint id, bool extended, int length)
    {
        var f = CanFrame.Parse(text);
        Assert.Equal(id, f.Id);
        Assert.Equal(extended, f.IsExtended);
        Assert.Equal(length, f.Data.Length);
        Assert.Equal(text, f.ToString());
    }

    [Fact]
    public void Can_fd_frames_use_valid_lengths()
    {
        var f = CanFrame.Parse("123##1" + new string('A', 24));
        Assert.True(f.IsFd);
        Assert.Equal(CanFrameFlags.Fd | CanFrameFlags.BitRateSwitch, f.Flags);
        Assert.Equal(12, f.Data.Length);
        Assert.Equal(9, f.Dlc);
        Assert.Throws<ArgumentException>(() => new CanFrame(0x123, new byte[10], CanFrameFlags.Fd));
        Assert.Equal(12, CanDlc.Pad(new byte[10]).Length);
        Assert.Equal(64, CanDlc.ToLength(15));
        Assert.Equal(8, CanDlc.ToLength(15, fd: false));
    }

    [Theory]
    [InlineData("800#00")]      // 11-bit id too large
    [InlineData("123#123")]     // odd hex
    [InlineData("123#001122334455667788")] // 9 bytes classic
    [InlineData("12#00")]
    public void Invalid_frames_are_rejected(string text) => Assert.False(CanFrame.TryParse(text, out _));

    [Fact]
    public void Filters_match_identifier_and_width()
    {
        var f = CanFilter.Exact(0x7E8);
        Assert.True(f.Matches(CanFrame.Create(0x7E8, 1)));
        Assert.False(f.Matches(new CanFrame(0x7E8, new byte[] { 1 }, CanFrameFlags.Extended)));
        var range = new CanFilter(0x7E8, 0x7F8); // 7E8–7EF
        Assert.True(range.Matches(CanFrame.Create(0x7EF)));
        Assert.False(range.Matches(CanFrame.Create(0x7E0)));
    }
}

public sealed class VirtualCanBusTests
{
    [Fact]
    public async Task Frames_reach_other_nodes_through_filters()
    {
        var net = new VirtualCanNetwork();
        await using var a = net.CreateNode();
        await using var b = net.CreateNode();
        await using var c = net.CreateNode(o => o.ReceiveOwnMessages = true);
        await a.ConnectAsync();
        await b.ConnectAsync();
        await c.ConnectAsync();
        using var all = b.OpenReader();
        using var only7e8 = b.OpenReader(CanFilter.Exact(0x7E8));
        using var own = c.OpenReader();

        await a.SendAsync(CanFrame.Parse("7E0#0322F190"));
        await a.SendAsync(CanFrame.Parse("7E8#0562F19001"));
        await c.SendAsync(CanFrame.Parse("100#01"));

        Assert.Equal("7E0#0322F190", (await all.ReadAsync()).ToString());
        Assert.Equal("7E8#0562F19001", (await all.ReadAsync()).ToString());
        Assert.Equal("100#01", (await all.ReadAsync()).ToString());
        Assert.Equal("7E8#0562F19001", (await only7e8.ReadAsync()).ToString());
        Assert.False(only7e8.TryRead(out _));
        Assert.Equal(3, (await Drain(own)).Count); // two from a + its own frame
        Assert.Equal(2, a.FramesSent);
        Assert.Equal(3, b.FramesReceived);
    }

    [Fact]
    public async Task Fd_frames_need_an_fd_bus()
    {
        await using var bus = new VirtualCanNetwork().CreateNode();
        await bus.ConnectAsync();
        await Assert.ThrowsAsync<NotSupportedException>(async () => await bus.SendAsync(CanFrame.Create(0x123, new byte[12])));
    }

    private static async Task<List<CanFrame>> Drain(CanReader r)
    {
        var list = new List<CanFrame>();
        while (await r.ReadAsync(TimeSpan.FromMilliseconds(50)) is { } f) list.Add(f);
        return list;
    }
}

public sealed class SlcanTests
{
    [Theory]
    [InlineData("123#DEADBEEF", "t1234DEADBEEF")]
    [InlineData("18DAF110#0322F190", "T18DAF11040322F190")]
    [InlineData("123#R", "r1230")]
    [InlineData("7E0##1" + "0011223344556677889900AA", "b7E09" + "0011223344556677889900AA")]
    public void Codec_matches_lawicel_notation(string frame, string line)
    {
        var f = CanFrame.Parse(frame);
        Assert.Equal(line, SlcanCodec.Encode(f));
        Assert.True(SlcanCodec.TryDecode(line, out var back));
        Assert.Equal(f, back);
        Assert.True(SlcanCodec.TryDecode(line + "1A2B", out var stamped)); // timestamp suffix
        Assert.Equal(f, stamped);
    }

    [Fact]
    public void Bitrate_commands()
    {
        Assert.Equal("S6", SlcanCodec.BitrateCommand(500_000));
        Assert.Equal("S8", SlcanCodec.BitrateCommand(1_000_000));
        Assert.Equal("Y2", SlcanCodec.DataBitrateCommand(2_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => SlcanCodec.BitrateCommand(333_000));
        Assert.False(SlcanCodec.TryDecode("t12", out _));
        Assert.False(SlcanCodec.TryDecode("t1239", out _)); // DLC 9 on classic
    }

    [Fact]
    public async Task Slcan_bus_talks_through_an_emulated_adapter()
    {
        var net = new VirtualCanNetwork();
        await using var peer = net.CreateNode(o => o.Fd = true);
        await peer.ConnectAsync();
        using var fromAdapter = peer.OpenReader();

        var (hostSide, adapterSide) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource();
        var adapter = SlcanAdapterSimulator.RunAsync(adapterSide, net, cts.Token);

        await using var bus = new SlcanBus(() => hostSide, "slcan:test", new CanBusOptions { Bitrate = 500_000, Fd = true });
        await bus.ConnectAsync();
        Assert.Equal(SlcanAdapterSimulator.Version, bus.AdapterVersion);
        using var reader = bus.OpenReader();

        await bus.SendAsync(CanFrame.Parse("7DF#02010C"));
        Assert.Equal("7DF#02010C", (await fromAdapter.ReadAsync()).ToString());

        await peer.SendAsync(CanFrame.Parse("7E8#04410C1AF8"));
        await peer.SendAsync(CanFrame.Parse("18DAF110##1" + new string('5', 32)));
        Assert.Equal("7E8#04410C1AF8", (await reader.ReadAsync()).ToString());
        Assert.Equal(16, (await reader.ReadAsync()).Data.Length);
        Assert.Equal(0, bus.AdapterErrors);

        await bus.DisposeAsync();
        await cts.CancelAsync();
        await adapter;
    }
}

public sealed class SocketCanTests
{
    [Fact]
    public void Kernel_struct_layout_round_trips()
    {
        Span<byte> raw = stackalloc byte[72];
        var classic = CanFrame.Parse("18DAF110#0322F190");
        Assert.Equal(16, SocketCanBus.Encode(classic, raw));
        Assert.Equal(new byte[] { 0x10, 0xF1, 0xDA, 0x98, 4 }, raw[..5].ToArray()); // little-endian id | EFF
        Assert.Equal(classic, SocketCanBus.Decode(raw[..16]));

        var fd = CanFrame.Parse("123##1" + new string('7', 128));
        Assert.Equal(72, SocketCanBus.Encode(fd, raw));
        Assert.Equal(0x05, raw[5]); // FDF | BRS
        Assert.Equal(fd, SocketCanBus.Decode(raw));
    }

    [Fact]
    public async Task Vcan_round_trip_when_available()
    {
        // Runs on Linux with a vcan0 interface (CI: sudo modprobe vcan && sudo ip link add vcan0 type vcan && sudo ip link set vcan0 up).
        if (!SocketCanBus.IsSupported || !SocketCanBus.ListInterfaces().Contains("vcan0")) return;
        await using var a = new SocketCanBus("vcan0");
        await using var b = new SocketCanBus("vcan0");
        await a.ConnectAsync();
        await b.ConnectAsync();
        using var r = b.OpenReader(CanFilter.Exact(0x321));
        await a.SendAsync(CanFrame.Parse("321#CAFE"));
        Assert.Equal("321#CAFE", (await r.ReadAsync(TimeSpan.FromSeconds(2)))?.ToString());
    }
}
