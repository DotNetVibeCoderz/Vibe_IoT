using System.Globalization;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IoTCom.Net.Protocols.Mdns;

/// <summary>DNS record types used by mDNS / DNS-SD.</summary>
public enum DnsType : ushort
{
    /// <summary>IPv4 address.</summary>
    A = 1,
    /// <summary>Pointer (service instance enumeration).</summary>
    Ptr = 12,
    /// <summary>Text (service metadata key=value).</summary>
    Txt = 16,
    /// <summary>IPv6 address.</summary>
    Aaaa = 28,
    /// <summary>Service location (target host and port).</summary>
    Srv = 33,
    /// <summary>Next secure (used by mDNS for negative answers).</summary>
    Nsec = 47,
    /// <summary>Any.</summary>
    Any = 255,
}

/// <summary>A question.</summary>
/// <param name="Name">Name (e.g. "_http._tcp.local").</param>
/// <param name="Type">Type.</param>
/// <param name="UnicastResponse">mDNS QU bit: the asker prefers a unicast reply.</param>
public sealed record DnsQuestion(string Name, DnsType Type, bool UnicastResponse = false);

/// <summary>A resource record.</summary>
public sealed record DnsRecord
{
    /// <summary>Owner name.</summary>
    public required string Name { get; init; }

    /// <summary>Type.</summary>
    public DnsType Type { get; init; }

    /// <summary>mDNS cache-flush bit (the record replaces older ones).</summary>
    public bool CacheFlush { get; init; }

    /// <summary>Time to live (0 = goodbye).</summary>
    public uint Ttl { get; init; } = 120;

    /// <summary>A/AAAA address.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>PTR target or SRV target host.</summary>
    public string? Target { get; init; }

    /// <summary>SRV port.</summary>
    public ushort Port { get; init; }

    /// <summary>SRV priority.</summary>
    public ushort Priority { get; init; }

    /// <summary>SRV weight.</summary>
    public ushort Weight { get; init; }

    /// <summary>TXT strings.</summary>
    public IReadOnlyList<string> Text { get; init; } = [];

    /// <summary>Raw data of other types.</summary>
    public byte[] Data { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() => Type switch
    {
        DnsType.A or DnsType.Aaaa => $"{Name} {Type} {Address} ttl {Ttl}",
        DnsType.Ptr => $"{Name} PTR {Target} ttl {Ttl}",
        DnsType.Srv => $"{Name} SRV {Target}:{Port} ttl {Ttl}",
        DnsType.Txt => $"{Name} TXT [{string.Join("; ", Text)}] ttl {Ttl}",
        _ => $"{Name} {Type} {Data.Length} B ttl {Ttl}",
    };
}

/// <summary>A DNS message (RFC 1035) as used by mDNS (RFC 6762): header, questions and records, with name compression.</summary>
public sealed record DnsMessage
{
    /// <summary>Transaction id (0 for mDNS).</summary>
    public ushort Id { get; init; }

    /// <summary>A response (QR bit).</summary>
    public bool IsResponse { get; init; }

    /// <summary>Authoritative answer (set by mDNS responders).</summary>
    public bool Authoritative { get; init; }

    /// <summary>Questions.</summary>
    public IReadOnlyList<DnsQuestion> Questions { get; init; } = [];

    /// <summary>Answers.</summary>
    public IReadOnlyList<DnsRecord> Answers { get; init; } = [];

    /// <summary>Authority records (probing).</summary>
    public IReadOnlyList<DnsRecord> Authorities { get; init; } = [];

    /// <summary>Additional records.</summary>
    public IReadOnlyList<DnsRecord> Additionals { get; init; } = [];

    /// <summary>Answers and additional records together.</summary>
    public IEnumerable<DnsRecord> AllRecords => Answers.Concat(Authorities).Concat(Additionals);

    /// <summary>Encodes the message with name compression.</summary>
    public byte[] Encode()
    {
        var w = new Writer();
        w.U16(Id);
        w.U16((ushort)((IsResponse ? 0x8000 : 0) | (Authoritative ? 0x0400 : 0)));
        w.U16((ushort)Questions.Count);
        w.U16((ushort)Answers.Count);
        w.U16((ushort)Authorities.Count);
        w.U16((ushort)Additionals.Count);
        foreach (var q in Questions)
        {
            w.Name(q.Name);
            w.U16((ushort)q.Type);
            w.U16((ushort)(1 | (q.UnicastResponse ? 0x8000 : 0)));
        }

        foreach (var r in Answers.Concat(Authorities).Concat(Additionals)) w.Record(r);
        return w.ToArray();
    }

    /// <summary>Decodes a message; never throws for malformed input.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out DnsMessage? message, out string? error)
    {
        (message, error) = (null, null);
        try
        {
            message = Decode(data);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Decodes a message or throws <see cref="FormatException"/>.</summary>
    public static DnsMessage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new FormatException("A DNS message has a 12-byte header.");
        var r = new Reader(data.ToArray());
        var id = r.U16();
        var flags = r.U16();
        int qd = r.U16(), an = r.U16(), ns = r.U16(), ar = r.U16();
        if (qd + an + ns + ar > data.Length / 4) throw new FormatException("DNS counts exceed the message size.");
        var questions = new List<DnsQuestion>(qd);
        for (var i = 0; i < qd; i++)
        {
            var name = r.Name();
            var type = (DnsType)r.U16();
            var cls = r.U16();
            questions.Add(new DnsQuestion(name, type, (cls & 0x8000) != 0));
        }

        List<DnsRecord> Records(int n)
        {
            var list = new List<DnsRecord>(n);
            for (var i = 0; i < n; i++) list.Add(r.Record());
            return list;
        }

        return new DnsMessage
        {
            Id = id,
            IsResponse = (flags & 0x8000) != 0,
            Authoritative = (flags & 0x0400) != 0,
            Questions = questions,
            Answers = Records(an),
            Authorities = Records(ns),
            Additionals = Records(ar),
        };
    }

    private sealed class Writer
    {
        private readonly List<byte> _b = [];
        private readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);

        public void U16(ushort v)
        {
            _b.Add((byte)(v >> 8));
            _b.Add((byte)v);
        }

        public void U32(uint v)
        {
            U16((ushort)(v >> 16));
            U16((ushort)v);
        }

        public void Name(string name)
        {
            var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < labels.Length; i++)
            {
                var suffix = string.Join('.', labels[i..]);
                if (_names.TryGetValue(suffix, out var at))
                {
                    U16((ushort)(0xC000 | at));
                    return;
                }

                if (_b.Count < 0x3FFF) _names[suffix] = _b.Count;
                var bytes = Encoding.UTF8.GetBytes(labels[i]);
                if (bytes.Length > 63) throw new ArgumentException($"DNS label longer than 63 bytes: {labels[i]}");
                _b.Add((byte)bytes.Length);
                _b.AddRange(bytes);
            }

            _b.Add(0);
        }

        public void Record(DnsRecord r)
        {
            Name(r.Name);
            U16((ushort)r.Type);
            U16((ushort)(1 | (r.CacheFlush ? 0x8000 : 0)));
            U32(r.Ttl);
            var lengthAt = _b.Count;
            U16(0);
            var start = _b.Count;
            switch (r.Type)
            {
                case DnsType.A or DnsType.Aaaa:
                    _b.AddRange(r.Address!.GetAddressBytes());
                    break;
                case DnsType.Ptr:
                    Name(r.Target!);
                    break;
                case DnsType.Srv:
                    U16(r.Priority);
                    U16(r.Weight);
                    U16(r.Port);
                    Name(r.Target!);
                    break;
                case DnsType.Txt:
                    if (r.Text.Count == 0) _b.Add(0);
                    foreach (var t in r.Text)
                    {
                        var bytes = Encoding.UTF8.GetBytes(t);
                        if (bytes.Length > 255) throw new ArgumentException("A TXT string is at most 255 bytes.");
                        _b.Add((byte)bytes.Length);
                        _b.AddRange(bytes);
                    }

                    break;
                default:
                    _b.AddRange(r.Data);
                    break;
            }

            var length = _b.Count - start;
            _b[lengthAt] = (byte)(length >> 8);
            _b[lengthAt + 1] = (byte)length;
        }

        public byte[] ToArray() => [.. _b];
    }

    /// <summary>Frame-lane fields: header, counts, then one field per question and record.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return [new FrameField("Invalid", 0, data.Length, FrameFieldKind.Error, "shorter than the 12-byte header")];
        var fields = new List<FrameField>
        {
            new("ID", 0, 2, FrameFieldKind.Address, BinaryPrimitives.ReadUInt16BigEndian(data).ToString(CultureInfo.InvariantCulture)),
            new("Flags", 2, 2, FrameFieldKind.Function, (data[2] & 0x80) != 0 ? "response" : "query"),
            new("Counts", 4, 8, FrameFieldKind.Length, $"QD {BinaryPrimitives.ReadUInt16BigEndian(data[4..])} AN {BinaryPrimitives.ReadUInt16BigEndian(data[6..])} NS {BinaryPrimitives.ReadUInt16BigEndian(data[8..])} AR {BinaryPrimitives.ReadUInt16BigEndian(data[10..])}"),
        };
        var r = new Reader(data.ToArray());
        try
        {
            r.Skip(4);
            int qd = r.U16(), an = r.U16(), ns = r.U16(), ar = r.U16();
            for (var i = 0; i < qd; i++)
            {
                var start = r.Position;
                var name = r.Name();
                var type = (DnsType)r.U16();
                r.U16();
                fields.Add(new FrameField("Question", start, r.Position - start, FrameFieldKind.Header, $"{name} {type}"));
            }

            for (var i = 0; i < an + ns + ar; i++)
            {
                var start = r.Position;
                var rec = r.Record();
                fields.Add(new FrameField(i < an ? "Answer" : i < an + ns ? "Authority" : "Additional", start, r.Position - start, FrameFieldKind.Data, rec.ToString()));
            }
        }
        catch (FormatException ex)
        {
            fields.Add(new FrameField("Invalid", r.Position, data.Length - r.Position, FrameFieldKind.Error, ex.Message));
        }

        return fields;
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;

        public int Position => _pos;

        public void Skip(int n) => _pos = Math.Min(data.Length, _pos + n);

        public ushort U16()
        {
            if (_pos + 2 > data.Length) throw new FormatException("Truncated DNS message.");
            var v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_pos));
            _pos += 2;
            return v;
        }

        public uint U32() => ((uint)U16() << 16) | U16();

        public string Name() => ReadName(ref _pos, 0);

        private string ReadName(ref int pos, int depth)
        {
            if (depth > 16) throw new FormatException("DNS name compression loop.");
            var labels = new List<string>();
            while (true)
            {
                if (pos >= data.Length) throw new FormatException("Truncated DNS name.");
                var len = data[pos];
                if (len == 0)
                {
                    pos++;
                    break;
                }

                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 2 > data.Length) throw new FormatException("Truncated DNS pointer.");
                    var target = ((len & 0x3F) << 8) | data[pos + 1];
                    pos += 2;
                    if (target >= data.Length) throw new FormatException("DNS pointer past the end.");
                    var p = target;
                    var rest = ReadName(ref p, depth + 1);
                    if (rest.Length > 0) labels.Add(rest);
                    break;
                }

                if ((len & 0xC0) != 0 || pos + 1 + len > data.Length) throw new FormatException("Invalid DNS label.");
                labels.Add(Encoding.UTF8.GetString(data, pos + 1, len));
                pos += 1 + len;
                if (labels.Count > 128) throw new FormatException("DNS name too long.");
            }

            return string.Join('.', labels);
        }

        public DnsRecord Record()
        {
            var name = Name();
            var type = (DnsType)U16();
            var cls = U16();
            var ttl = U32();
            var length = U16();
            if (_pos + length > data.Length) throw new FormatException("Truncated DNS record data.");
            var end = _pos + length;
            var record = new DnsRecord { Name = name, Type = type, CacheFlush = (cls & 0x8000) != 0, Ttl = ttl };
            switch (type)
            {
                case DnsType.A when length == 4:
                case DnsType.Aaaa when length == 16:
                    record = record with { Address = new IPAddress(data.AsSpan(_pos, length)) };
                    break;
                case DnsType.Ptr:
                {
                    var p = _pos;
                    record = record with { Target = ReadName(ref p, 0) };
                    break;
                }

                case DnsType.Srv when length >= 7:
                {
                    var p = _pos + 6;
                    record = record with
                    {
                        Priority = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_pos)),
                        Weight = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_pos + 2)),
                        Port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(_pos + 4)),
                        Target = ReadName(ref p, 0),
                    };
                    break;
                }

                case DnsType.Txt:
                {
                    var text = new List<string>();
                    var p = _pos;
                    while (p < end)
                    {
                        var n = data[p];
                        if (p + 1 + n > end) throw new FormatException("Truncated TXT string.");
                        if (n > 0) text.Add(Encoding.UTF8.GetString(data, p + 1, n));
                        p += 1 + n;
                    }

                    record = record with { Text = text };
                    break;
                }

                default:
                    record = record with { Data = data[_pos..end] };
                    break;
            }

            _pos = end;
            return record;
        }
    }
}

/// <summary>Helpers for addresses and multicast endpoints.</summary>
public static class MdnsAddresses
{
    /// <summary>The IPv4 mDNS group 224.0.0.251.</summary>
    public static IPAddress Group4 { get; } = IPAddress.Parse("224.0.0.251");

    /// <summary>The IPv6 mDNS group ff02::fb.</summary>
    public static IPAddress Group6 { get; } = IPAddress.Parse("ff02::fb");

    /// <summary>The mDNS port.</summary>
    public const int Port = 5353;

    /// <summary>224.0.0.251:5353.</summary>
    public static IPEndPoint Endpoint4 { get; } = new(Group4, Port);

    /// <summary>Unicast IPv4 addresses of this machine's up interfaces (no loopback).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try
        {
            return [.. System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)];
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }
}
