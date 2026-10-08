using System.Buffers.Binary;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Tests.Framing;

public sealed class PcapngTests
{
    private sealed record Block(uint Type, byte[] Body);

    /// <summary>Parses pcapng blocks, checking that the leading and trailing lengths agree.</summary>
    private static List<Block> Parse(byte[] file)
    {
        var blocks = new List<Block>();
        for (var pos = 0; pos < file.Length;)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
            var len = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos + 4));
            Assert.True(len % 4 == 0 && len >= 12, $"block length {len}");
            Assert.Equal(len, BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos + len - 4)));
            blocks.Add(new Block(type, file.AsSpan(pos + 8, len - 12).ToArray()));
            pos += len;
        }
        return blocks;
    }

    private static ushort Fold(ReadOnlySpan<byte> data, uint sum = 0)
    {
        for (var i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (data.Length % 2 == 1) sum += (uint)(data[^1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)sum;
    }

    [Fact]
    public void Writes_a_valid_capture_with_native_encapsulations()
    {
        var modbusRequest = Convert.FromHexString("000100000006" + "01030000000A");           // MBAP + read holding 0..10
        var modbusResponse = Convert.FromHexString("000100000007" + "010304002A002B");
        var coap = new CoapMessage { Code = CoapCode.Get, MessageId = 0x7D34, UriPath = "/temperature" }.Encode();
        var can = CanBusBase.ToTapBytes(CanFrame.Parse("7E8#0441 0C1AF8".Replace(" ", "", StringComparison.Ordinal)));
        var t0 = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        using var ms = new MemoryStream();
        using (var tap = new PcapngTap(new PcapngWriter(ms, leaveOpen: true)))
        {
            tap.OnFrame(new TrafficFrame("modbus-tcp", FrameDirection.Outbound, modbusRequest, t0, summary: "Read Holding Registers"));
            tap.OnFrame(new TrafficFrame("modbus-tcp", FrameDirection.Inbound, modbusResponse, t0.AddMilliseconds(3.5)));
            tap.OnFrame(new TrafficFrame("coap", FrameDirection.Outbound, coap, t0.AddSeconds(1)));
            tap.OnFrame(new TrafficFrame("can", FrameDirection.Inbound, can, t0.AddSeconds(2)));
            tap.OnFrame(new TrafficFrame("uds", FrameDirection.Outbound, new byte[] { 0x22, 0xF1, 0x90 }, t0.AddSeconds(3), summary: "ReadDataByIdentifier"));
            Assert.Equal(5, tap.PacketCount);
        }
        var file = ms.ToArray();
        if (Environment.GetEnvironmentVariable("IOTCOM_PCAP_OUT") is { Length: > 0 } outPath) File.WriteAllBytes(outPath, file); // CI: tshark gate

        var blocks = Parse(file);
        Assert.Equal(0x0A0D0D0Au, blocks[0].Type);
        Assert.Equal(0x1A2B3C4Du, BinaryPrimitives.ReadUInt32LittleEndian(blocks[0].Body));
        var linkTypes = blocks.Where(b => b.Type == 1).Select(b => BinaryPrimitives.ReadUInt16LittleEndian(b.Body)).ToList();
        Assert.Equal([PcapLinkType.RawIp, PcapLinkType.CanSocketCan, PcapLinkType.User0], linkTypes);
        var packets = blocks.Where(b => b.Type == 6).ToList();
        Assert.Equal(5, packets.Count);

        // Packet 1: IPv4 + TCP to port 502 with valid checksums; the payload is the Modbus ADU.
        var p1 = packets[0].Body;
        var caplen = BinaryPrimitives.ReadInt32LittleEndian(p1.AsSpan(12));
        var ip = p1.AsSpan(20, caplen);
        Assert.Equal(0x45, ip[0]);
        Assert.Equal(0xFFFF, Fold(ip[..20]));                     // IPv4 header checksum
        Assert.Equal(6, ip[9]);
        Assert.Equal(502, BinaryPrimitives.ReadUInt16BigEndian(ip[22..]));
        Span<byte> pseudo = [.. ip.Slice(12, 8), 0, 6, (byte)((caplen - 20) >> 8), (byte)(caplen - 20)];
        Assert.Equal(0xFFFF, Fold(ip[20..], Fold(pseudo)));        // TCP checksum
        Assert.Equal(modbusRequest, ip[40..].ToArray());
        var micros = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(p1.AsSpan(4)) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(p1.AsSpan(8));
        Assert.Equal((ulong)t0.ToUnixTimeMilliseconds() * 1000, micros);

        // Packet 2 (response) acknowledges the request's bytes and comes from port 502.
        var ip2 = packets[1].Body.AsSpan(20);
        Assert.Equal(502, BinaryPrimitives.ReadUInt16BigEndian(ip2[20..]));
        Assert.Equal(1u + (uint)modbusRequest.Length, BinaryPrimitives.ReadUInt32BigEndian(ip2[28..])); // ack
        // Packet 3: UDP 5683; packet 4: the SocketCAN bytes verbatim; packet 5: user data with the summary as comment.
        Assert.Equal(17, packets[2].Body[20 + 9]);
        Assert.Equal(5683, BinaryPrimitives.ReadUInt16BigEndian(packets[2].Body.AsSpan(20 + 22)));
        Assert.Equal(can, packets[3].Body.AsSpan(20, can.Length).ToArray());
        Assert.Contains("uds: ReadDataByIdentifier", System.Text.Encoding.UTF8.GetString(packets[4].Body));
    }

    [Fact]
    public async Task Captures_a_live_modbus_session()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iotcom-{Guid.NewGuid():N}.pcapng");
        try
        {
            var listener = new Transports.InMemoryTransportListener();
            var store = new ModbusDataStore();
            await using var server = ModbusServer.Create(o => o.ListenInMemory(listener).WithStore(store));
            await server.StartAsync();
            using (var pcap = PcapngTap.Create(path))
            {
                await using var client = ModbusClient.Create(o => o.UseInMemory(listener).WithTap(pcap));
                await client.ConnectAsync();
                for (var i = 0; i < 5; i++) await client.ReadHoldingRegistersAsync(0, 4);
                Assert.Equal(10, pcap.PacketCount);
            }
            Assert.Equal(1 + 1 + 10, Parse(await File.ReadAllBytesAsync(path)).Count); // SHB + IDB + packets
        }
        finally
        {
            File.Delete(path);
        }
    }
}
