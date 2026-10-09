using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace IoTCom.Net;

/// <summary>Link-layer types used in pcapng interface descriptions.</summary>
public static class PcapLinkType
{
    /// <summary>Raw IPv4/IPv6 packets (LINKTYPE_RAW).</summary>
    public const ushort RawIp = 101;
    /// <summary>User-defined encapsulation 0 (LINKTYPE_USER0); Wireshark shows it as data unless configured.</summary>
    public const ushort User0 = 147;
    /// <summary>SocketCAN frames with a big-endian identifier (LINKTYPE_CAN_SOCKETCAN).</summary>
    public const ushort CanSocketCan = 227;
    /// <summary>LoRa frames with a LoRaTap header (LINKTYPE_LORATAP); Wireshark hands sync word 0x34 to its LoRaWAN dissector.</summary>
    public const ushort LoRaTap = 270;
}

/// <summary>
/// Minimal pcapng writer (little-endian): a section header, interface descriptions and enhanced packet blocks with
/// microsecond timestamps, an optional comment and the direction flag. Opens in Wireshark, tshark and tcpdump.
/// Not thread-safe — <see cref="PcapngTap"/> serialises access.
/// </summary>
public sealed class PcapngWriter : IDisposable
{
    private const uint SectionHeader = 0x0A0D0D0A, InterfaceDescription = 0x00000001, EnhancedPacket = 0x00000006;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private int _interfaces;

    /// <summary>Writes a section header to <paramref name="stream"/>.</summary>
    public PcapngWriter(Stream stream, string application = "IoTCom.Net", bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        var options = new List<byte>();
        AddOption(options, 4, Encoding.UTF8.GetBytes($"{application} {IoTComInfo.Version}")); // shb_userappl
        var body = new byte[16 + options.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x1A2B3C4D);      // byte-order magic
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1);     // version 1.0
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 0);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), -1);     // section length unknown
        options.CopyTo(body, 16);
        WriteBlock(SectionHeader, body);
    }

    /// <summary>Number of packets written.</summary>
    public long PacketCount { get; private set; }

    /// <summary>Adds an interface and returns its id (timestamps in microseconds).</summary>
    public int AddInterface(ushort linkType, string name, uint snapLength = 262_144)
    {
        var options = new List<byte>();
        AddOption(options, 2, Encoding.UTF8.GetBytes(name));     // if_name
        AddOption(options, 9, [6]);                              // if_tsresol: 10^-6
        var body = new byte[8 + options.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(body, linkType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), snapLength);
        options.CopyTo(body, 8);
        WriteBlock(InterfaceDescription, body);
        return _interfaces++;
    }

    /// <summary>Writes one packet. <paramref name="inbound"/> sets the epb_flags direction (null = unknown).</summary>
    public void WritePacket(int interfaceId, DateTimeOffset timestamp, ReadOnlySpan<byte> data, bool? inbound = null, string? comment = null)
    {
        if ((uint)interfaceId >= (uint)_interfaces) throw new ArgumentOutOfRangeException(nameof(interfaceId));
        var options = new List<byte>();
        if (comment is { Length: > 0 }) AddOption(options, 1, Encoding.UTF8.GetBytes(comment));         // opt_comment
        if (inbound is { } dir) AddOption(options, 2, BitConverter.GetBytes(dir ? 1u : 2u));         // epb_flags: 01 inbound, 10 outbound
        var padded = (data.Length + 3) & ~3;
        var body = new byte[20 + padded + options.Count];
        var micros = (ulong)(timestamp.ToUnixTimeMilliseconds() * 1000 + timestamp.Ticks % TimeSpan.TicksPerMillisecond / 10);
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)interfaceId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)(micros >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)micros);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)data.Length);   // captured length
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)data.Length);   // original length
        data.CopyTo(body.AsSpan(20));
        options.CopyTo(body, 20 + padded);
        WriteBlock(EnhancedPacket, body);
        PacketCount++;
    }

    /// <summary>Flushes buffered data.</summary>
    public void Flush() => _stream.Flush();

    private static void AddOption(List<byte> options, ushort code, ReadOnlySpan<byte> value)
    {
        Span<byte> head = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(head, code);
        BinaryPrimitives.WriteUInt16LittleEndian(head[2..], (ushort)value.Length);
        options.AddRange(head.ToArray());
        options.AddRange(value.ToArray());
        for (var i = value.Length; i % 4 != 0; i++) options.Add(0);
    }

    private void WriteBlock(uint type, byte[] body)
    {
        // Every non-empty option list ends with opt_endofopt (4 zero bytes); appended here when the body ends with options.
        var hasOptions = type switch
        {
            SectionHeader => body.Length > 16,
            InterfaceDescription => body.Length > 8,
            _ => body.Length > 20 + ((BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12)) + 3) & ~3),
        };
        var total = 12 + body.Length + (hasOptions ? 4 : 0);
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, type);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)total);
        _stream.Write(head);
        _stream.Write(body);
        if (hasOptions) _stream.Write(stackalloc byte[4]);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, (uint)total);
        _stream.Write(tail);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stream.Flush();
        if (!_leaveOpen) _stream.Dispose();
    }
}

/// <summary>
/// A traffic tap that writes every frame to a pcapng file Wireshark can dissect. CAN frames use the SocketCAN link type;
/// frames of TCP- and UDP-based protocols are wrapped in synthetic IPv4 + TCP/UDP headers on the protocol's well-known
/// port (local 10.0.0.1, peer 10.0.0.2, consistent TCP sequence numbers); anything else is written as user-defined
/// data with the protocol and decoded summary as the packet comment.
/// </summary>
/// <example>
/// <code>
/// using var pcap = PcapngTap.Create("capture.pcapng");
/// client.AddTap(pcap);       // …later open capture.pcapng in Wireshark
/// </code>
/// </example>
public sealed class PcapngTap : ITrafficTap, IDisposable
{
    private static readonly IPAddress Local = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Peer = IPAddress.Parse("10.0.0.2");
    private readonly PcapngWriter _writer;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _interfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, bool), uint> _tcpSeq = new();
    private ushort _ipId;
    private bool _disposed;

    /// <summary>Creates a tap writing to <paramref name="writer"/>.</summary>
    public PcapngTap(PcapngWriter writer) => _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    /// <summary>
    /// Creates (overwrites) a pcapng file. Each packet is flushed, so the capture survives an abrupt stop and Wireshark can
    /// follow the file while it grows.
    /// </summary>
    public static PcapngTap Create(string path) => new(new PcapngWriter(File.Create(path))) { AutoFlush = true };

    /// <summary>Flush after every packet.</summary>
    public bool AutoFlush { get; init; }

    /// <summary>Packets written.</summary>
    public long PacketCount
    {
        get
        {
            lock (_gate) return _writer.PacketCount;
        }
    }

    /// <summary>How a protocol name is encapsulated: transport ("tcp", "udp", "can", "user") and well-known port.</summary>
    public static (string Transport, ushort Port) Encapsulation(string protocol) => protocol switch
    {
        "modbus-tcp" or "modbus-tcp-native" => ("tcp", 502),
        "mqtt-raw" => ("tcp", 1883),
        "nmea0183" => ("tcp", 10110),
        "hl7" => ("tcp", 2575),
        "iec104" => ("tcp", 2404),
        "ntp" => ("udp", 123),
        "coap" => ("udp", 5683),
        "artnet" => ("udp", 6454),
        "sacn" => ("udp", 5568),
        "mavlink" => ("udp", 14550),
        "can" or "can-slcan" => ("can", 0),
        "lorawan" or "semtech-udp" => ("loratap", 0),
        _ => ("user", 0),
    };

    /// <inheritdoc />
    public void OnFrame(in TrafficFrame frame)
    {
        var (transport, port) = Encapsulation(frame.Protocol);
        var inbound = frame.Direction == FrameDirection.Inbound;
        var comment = frame.Summary is { Length: > 0 } s ? $"{frame.Protocol}: {s}" : frame.Protocol;
        lock (_gate)
        {
            if (_disposed) return;
            switch (transport)
            {
                case "can":
                    _writer.WritePacket(Interface("can", PcapLinkType.CanSocketCan), frame.Timestamp, frame.Data.Span, inbound, comment);
                    break;
                case "loratap":
                    _writer.WritePacket(Interface("lora", PcapLinkType.LoRaTap), frame.Timestamp, LoRaTap(frame.Data.Span), inbound, comment);
                    break;
                case "tcp" or "udp":
                    var packet = BuildIpPacket(transport == "tcp", port, inbound, frame.Data.Span, frame.Protocol);
                    _writer.WritePacket(Interface("ip", PcapLinkType.RawIp), frame.Timestamp, packet, inbound, comment);
                    break;
                default:
                    _writer.WritePacket(Interface("iotcom", PcapLinkType.User0), frame.Timestamp, frame.Data.Span, inbound, comment);
                    break;
            }
            if (AutoFlush) _writer.Flush();
        }
    }

    // LoRaTap v0 header (15 bytes): version, padding, length (BE), frequency (Hz, BE; 0 = unknown), bandwidth
    // (×125 kHz), SF, packet/max/current RSSI, SNR, sync word 0x34 (public LoRaWAN).
    private static byte[] LoRaTap(ReadOnlySpan<byte> phy)
    {
        var packet = new byte[15 + phy.Length];
        packet[3] = 15;
        packet[8] = 1;
        packet[9] = 7;
        packet[14] = 0x34;
        phy.CopyTo(packet.AsSpan(15));
        return packet;
    }

    private int Interface(string name, ushort linkType)
    {
        if (!_interfaces.TryGetValue(name, out var id)) _interfaces[name] = id = _writer.AddInterface(linkType, name);
        return id;
    }

    /// <summary>IPv4 + TCP/UDP around <paramref name="payload"/>: outbound = 10.0.0.1:49152 → 10.0.0.2:port.</summary>
    private byte[] BuildIpPacket(bool tcp, ushort port, bool inbound, ReadOnlySpan<byte> payload, string flow)
    {
        var l4 = tcp ? 20 : 8;
        var packet = new byte[20 + l4 + payload.Length];
        var (src, dst) = inbound ? (Peer, Local) : (Local, Peer);
        var (sport, dport) = inbound ? (port, (ushort)49152) : ((ushort)49152, port);

        // IPv4 header.
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), ++_ipId);
        packet[6] = 0x40;               // don't fragment
        packet[8] = 64;                 // TTL
        packet[9] = (byte)(tcp ? 6 : 17);
        src.TryWriteBytes(packet.AsSpan(12), out _);
        dst.TryWriteBytes(packet.AsSpan(16), out _);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), Checksum(packet.AsSpan(0, 20), 0));

        var seg = packet.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(seg, sport);
        BinaryPrimitives.WriteUInt16BigEndian(seg[2..], dport);
        if (tcp)
        {
            // Sequence numbers advance per direction so Wireshark reassembles the stream; ACK = the other side's next seq.
            var seq = _tcpSeq.GetValueOrDefault((flow, inbound), 1u);
            var ack = _tcpSeq.GetValueOrDefault((flow, !inbound), 1u);
            _tcpSeq[(flow, inbound)] = seq + (uint)payload.Length;
            BinaryPrimitives.WriteUInt32BigEndian(seg[4..], seq);
            BinaryPrimitives.WriteUInt32BigEndian(seg[8..], ack);
            seg[12] = 0x50;             // data offset 5 words
            seg[13] = 0x18;             // PSH | ACK
            BinaryPrimitives.WriteUInt16BigEndian(seg[14..], 64_240);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(seg[4..], (ushort)(8 + payload.Length));
        }
        payload.CopyTo(seg[l4..]);

        // Transport checksum over the pseudo-header.
        Span<byte> pseudo = stackalloc byte[12];
        src.TryWriteBytes(pseudo, out _);
        dst.TryWriteBytes(pseudo[4..], out _);
        pseudo[9] = packet[9];
        BinaryPrimitives.WriteUInt16BigEndian(pseudo[10..], (ushort)seg.Length);
        var sum = Checksum(seg, Sum(pseudo, 0));
        if (!tcp && sum == 0) sum = 0xFFFF;
        BinaryPrimitives.WriteUInt16BigEndian(seg[(tcp ? 16 : 6)..], sum);
        return packet;
    }

    private static uint Sum(ReadOnlySpan<byte> data, uint sum)
    {
        for (var i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (data.Length % 2 == 1) sum += (uint)(data[^1] << 8);
        return sum;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data, uint initial)
    {
        var sum = Sum(data, initial);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>Flushes the file.</summary>
    public void Flush()
    {
        lock (_gate) _writer.Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
