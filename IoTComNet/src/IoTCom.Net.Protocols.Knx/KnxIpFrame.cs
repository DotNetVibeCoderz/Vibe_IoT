using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>KNXnet/IP service type identifiers (KNX Standard 3/8/2).</summary>
public enum KnxServiceType : ushort
{
    /// <summary>SEARCH_REQUEST.</summary>
    SearchRequest = 0x0201,
    /// <summary>SEARCH_RESPONSE.</summary>
    SearchResponse = 0x0202,
    /// <summary>DESCRIPTION_REQUEST.</summary>
    DescriptionRequest = 0x0203,
    /// <summary>DESCRIPTION_RESPONSE.</summary>
    DescriptionResponse = 0x0204,
    /// <summary>CONNECT_REQUEST.</summary>
    ConnectRequest = 0x0205,
    /// <summary>CONNECT_RESPONSE.</summary>
    ConnectResponse = 0x0206,
    /// <summary>CONNECTIONSTATE_REQUEST (heartbeat).</summary>
    ConnectionStateRequest = 0x0207,
    /// <summary>CONNECTIONSTATE_RESPONSE.</summary>
    ConnectionStateResponse = 0x0208,
    /// <summary>DISCONNECT_REQUEST.</summary>
    DisconnectRequest = 0x0209,
    /// <summary>DISCONNECT_RESPONSE.</summary>
    DisconnectResponse = 0x020A,
    /// <summary>TUNNELING_REQUEST.</summary>
    TunnelingRequest = 0x0420,
    /// <summary>TUNNELING_ACK.</summary>
    TunnelingAck = 0x0421,
    /// <summary>ROUTING_INDICATION.</summary>
    RoutingIndication = 0x0530,
    /// <summary>ROUTING_LOST_MESSAGE.</summary>
    RoutingLostMessage = 0x0531,
    /// <summary>ROUTING_BUSY.</summary>
    RoutingBusy = 0x0532,
}

/// <summary>KNXnet/IP status codes.</summary>
public static class KnxIpStatus
{
    /// <summary>No error.</summary>
    public const byte NoError = 0x00;
    /// <summary>Unknown communication channel id.</summary>
    public const byte ConnectionId = 0x21;
    /// <summary>Requested connection type not supported.</summary>
    public const byte ConnectionType = 0x22;
    /// <summary>Requested connection option not supported.</summary>
    public const byte ConnectionOption = 0x23;
    /// <summary>All connections in use.</summary>
    public const byte NoMoreConnections = 0x24;
    /// <summary>Error in the data connection.</summary>
    public const byte DataConnection = 0x26;
    /// <summary>Error in the KNX subnetwork connection.</summary>
    public const byte KnxConnection = 0x27;
    /// <summary>Tunnelling layer not supported.</summary>
    public const byte TunnellingLayer = 0x29;

    /// <summary>Name of a status code.</summary>
    public static string Name(byte status) => status switch
    {
        NoError => "E_NO_ERROR",
        ConnectionId => "E_CONNECTION_ID",
        ConnectionType => "E_CONNECTION_TYPE",
        ConnectionOption => "E_CONNECTION_OPTION",
        NoMoreConnections => "E_NO_MORE_CONNECTIONS",
        DataConnection => "E_DATA_CONNECTION",
        KnxConnection => "E_KNX_CONNECTION",
        TunnellingLayer => "E_TUNNELLING_LAYER",
        _ => $"0x{status:X2}",
    };
}

/// <summary>A KNX IP device as described by a search or description response.</summary>
public sealed record KnxDeviceInfo
{
    /// <summary>KNX medium (0x02 = TP1).</summary>
    public byte Medium { get; init; } = 0x02;

    /// <summary>Individual address of the interface.</summary>
    public KnxIndividualAddress Address { get; init; }

    /// <summary>Project-installation identifier.</summary>
    public ushort ProjectInstallation { get; init; }

    /// <summary>KNX serial number (6 bytes).</summary>
    public byte[] SerialNumber { get; init; } = new byte[6];

    /// <summary>Routing multicast address.</summary>
    public IPAddress Multicast { get; init; } = KnxIp.RoutingMulticast;

    /// <summary>MAC address (6 bytes).</summary>
    public byte[] Mac { get; init; } = new byte[6];

    /// <summary>Friendly name (up to 30 ISO 8859-1 characters).</summary>
    public string FriendlyName { get; init; } = "";

    /// <summary>Supported service families: (family, version), e.g. (2 core, 1), (4 tunnelling, 1), (5 routing, 1).</summary>
    public IReadOnlyList<(byte Family, byte Version)> Services { get; init; } = [(2, 1), (3, 1), (4, 1), (5, 1)];

    /// <summary>Where the device answered from (control endpoint).</summary>
    public IPEndPoint? ControlEndpoint { get; init; }

    /// <summary>True when tunnelling is supported.</summary>
    public bool SupportsTunnelling => Services.Any(s => s.Family == 4);

    /// <summary>True when routing is supported.</summary>
    public bool SupportsRouting => Services.Any(s => s.Family == 5);

    /// <inheritdoc />
    public override string ToString() =>
        $"{FriendlyName} ({Address}) at {ControlEndpoint}, serial {Convert.ToHexString(SerialNumber)}{(SupportsTunnelling ? ", tunnelling" : "")}{(SupportsRouting ? ", routing" : "")}";
}

/// <summary>
/// KNXnet/IP frames (sans-I/O): header, HPAI, connection request/response information, device description blocks,
/// and the bodies of search, description, connect, connection state, disconnect, tunnelling and routing services.
/// </summary>
public static class KnxIp
{
    /// <summary>Well-known KNXnet/IP UDP port.</summary>
    public const int Port = 3671;

    /// <summary>Routing and discovery multicast group.</summary>
    public static IPAddress RoutingMulticast { get; } = IPAddress.Parse("224.0.23.12");

    /// <summary>Header size.</summary>
    public const int HeaderSize = 6;

    /// <summary>Builds a frame: header + body.</summary>
    public static byte[] Frame(KnxServiceType service, ReadOnlySpan<byte> body)
    {
        var b = new byte[HeaderSize + body.Length];
        b[0] = 0x06;
        b[1] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)service);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)b.Length);
        body.CopyTo(b.AsSpan(HeaderSize));
        return b;
    }

    /// <summary>Parses the header; returns the service and the body.</summary>
    public static (KnxServiceType Service, ReadOnlyMemory<byte> Body) Parse(ReadOnlyMemory<byte> frame)
    {
        var b = frame.Span;
        if (b.Length < HeaderSize) throw new ProtocolException("KNXnet/IP frame shorter than its 6-byte header.");
        if (b[0] != 0x06 || b[1] != 0x10) throw new ProtocolException($"KNXnet/IP header 0x{b[0]:X2} 0x{b[1]:X2} is not 06 10 (version 1.0).");
        var total = BinaryPrimitives.ReadUInt16BigEndian(b[4..]);
        if (total != b.Length) throw new ProtocolException($"KNXnet/IP total length {total} does not match the {b.Length} byte(s) received.");
        return ((KnxServiceType)BinaryPrimitives.ReadUInt16BigEndian(b[2..]), frame[HeaderSize..]);
    }

    /// <summary>Host protocol address information (UDP, 8 bytes). 0.0.0.0:0 means "answer to where the packet came from" (NAT).</summary>
    public static byte[] Hpai(IPEndPoint? endpoint)
    {
        var b = new byte[8];
        b[0] = 8;
        b[1] = 0x01;
        var ep = endpoint ?? new IPEndPoint(IPAddress.Any, 0);
        ep.Address.MapToIPv4().GetAddressBytes().CopyTo(b, 2);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)ep.Port);
        return b;
    }

    /// <summary>Reads an HPAI.</summary>
    public static IPEndPoint ReadHpai(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8 || b[0] != 8) throw new ProtocolException("HPAI must be 8 bytes.");
        if (b[1] != 0x01) throw new ProtocolException($"HPAI protocol 0x{b[1]:X2} is not UDP.");
        return new IPEndPoint(new IPAddress(b.Slice(2, 4)), BinaryPrimitives.ReadUInt16BigEndian(b[6..]));
    }

    /// <summary>Resolves an HPAI that may be 0.0.0.0:0 (route back) against the sender.</summary>
    public static EndPoint Route(IPEndPoint hpai, EndPoint sender) => hpai.Port == 0 || hpai.Address.Equals(IPAddress.Any) ? sender : hpai;

    // ---- bodies --------------------------------------------------------------------------------------------------------

    /// <summary>SEARCH_REQUEST.</summary>
    public static byte[] SearchRequest(IPEndPoint? discovery) => Frame(KnxServiceType.SearchRequest, Hpai(discovery));

    /// <summary>DESCRIPTION_REQUEST.</summary>
    public static byte[] DescriptionRequest(IPEndPoint? control) => Frame(KnxServiceType.DescriptionRequest, Hpai(control));

    /// <summary>SEARCH_RESPONSE.</summary>
    public static byte[] SearchResponse(IPEndPoint control, KnxDeviceInfo device) => Frame(KnxServiceType.SearchResponse, [.. Hpai(control), .. DeviceDib(device), .. ServicesDib(device)]);

    /// <summary>DESCRIPTION_RESPONSE.</summary>
    public static byte[] DescriptionResponse(KnxDeviceInfo device) => Frame(KnxServiceType.DescriptionResponse, [.. DeviceDib(device), .. ServicesDib(device)]);

    /// <summary>CONNECT_REQUEST for a tunnelling connection on the link layer.</summary>
    public static byte[] ConnectRequest(IPEndPoint? control, IPEndPoint? data) => Frame(KnxServiceType.ConnectRequest, [.. Hpai(control), .. Hpai(data), 4, 0x04, 0x02, 0x00]);

    /// <summary>CONNECT_RESPONSE.</summary>
    public static byte[] ConnectResponse(byte channel, byte status, IPEndPoint? data, KnxIndividualAddress assigned) => status != KnxIpStatus.NoError
        ? Frame(KnxServiceType.ConnectResponse, [channel, status])
        : Frame(KnxServiceType.ConnectResponse, [channel, status, .. Hpai(data), 4, 0x04, (byte)(assigned.Raw >> 8), (byte)assigned.Raw]);

    /// <summary>CONNECTIONSTATE_REQUEST.</summary>
    public static byte[] ConnectionStateRequest(byte channel, IPEndPoint? control) => Frame(KnxServiceType.ConnectionStateRequest, [channel, 0, .. Hpai(control)]);

    /// <summary>CONNECTIONSTATE_RESPONSE.</summary>
    public static byte[] ConnectionStateResponse(byte channel, byte status) => Frame(KnxServiceType.ConnectionStateResponse, [channel, status]);

    /// <summary>DISCONNECT_REQUEST.</summary>
    public static byte[] DisconnectRequest(byte channel, IPEndPoint? control) => Frame(KnxServiceType.DisconnectRequest, [channel, 0, .. Hpai(control)]);

    /// <summary>DISCONNECT_RESPONSE.</summary>
    public static byte[] DisconnectResponse(byte channel, byte status) => Frame(KnxServiceType.DisconnectResponse, [channel, status]);

    /// <summary>TUNNELING_REQUEST carrying a cEMI frame.</summary>
    public static byte[] TunnelingRequest(byte channel, byte sequence, ReadOnlySpan<byte> cemi) => Frame(KnxServiceType.TunnelingRequest, [4, channel, sequence, 0, .. cemi]);

    /// <summary>TUNNELING_ACK.</summary>
    public static byte[] TunnelingAck(byte channel, byte sequence, byte status) => Frame(KnxServiceType.TunnelingAck, [4, channel, sequence, status]);

    /// <summary>ROUTING_INDICATION carrying a cEMI frame.</summary>
    public static byte[] RoutingIndication(ReadOnlySpan<byte> cemi) => Frame(KnxServiceType.RoutingIndication, cemi);

    /// <summary>Reads a connection header (tunnelling request/ack): channel, sequence, status, payload after it.</summary>
    public static (byte Channel, byte Sequence, byte Status, ReadOnlyMemory<byte> Payload) ReadConnectionHeader(ReadOnlyMemory<byte> body)
    {
        var b = body.Span;
        if (b.Length < 4 || b[0] != 4) throw new ProtocolException("KNXnet/IP connection header must be 4 bytes.");
        return (b[1], b[2], b[3], body[4..]);
    }

    /// <summary>Reads a CONNECT_RESPONSE: channel, status, data endpoint and assigned address (when accepted).</summary>
    public static (byte Channel, byte Status, IPEndPoint? Data, KnxIndividualAddress Assigned) ReadConnectResponse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) throw new ProtocolException("CONNECT_RESPONSE too short.");
        if (body[1] != KnxIpStatus.NoError) return (body[0], body[1], null, default);
        if (body.Length < 14 || body[10] != 4 || body[11] != 0x04) throw new ProtocolException("CONNECT_RESPONSE without a tunnelling CRD.");
        return (body[0], body[1], ReadHpai(body[2..]), new KnxIndividualAddress(BinaryPrimitives.ReadUInt16BigEndian(body[12..])));
    }

    /// <summary>Device information DIB (54 bytes).</summary>
    public static byte[] DeviceDib(KnxDeviceInfo d)
    {
        var b = new byte[54];
        b[0] = 54;
        b[1] = 0x01;
        b[2] = d.Medium;
        b[3] = 0;                                            // programming mode off
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), d.Address.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), d.ProjectInstallation);
        d.SerialNumber.AsSpan(0, Math.Min(6, d.SerialNumber.Length)).CopyTo(b.AsSpan(8));
        d.Multicast.GetAddressBytes().CopyTo(b, 14);
        d.Mac.AsSpan(0, Math.Min(6, d.Mac.Length)).CopyTo(b.AsSpan(18));
        var name = Encoding.Latin1.GetBytes(d.FriendlyName.Length > 30 ? d.FriendlyName[..30] : d.FriendlyName);
        name.CopyTo(b, 24);
        return b;
    }

    /// <summary>Supported service families DIB.</summary>
    public static byte[] ServicesDib(KnxDeviceInfo d)
    {
        var b = new byte[2 + (2 * d.Services.Count)];
        b[0] = (byte)b.Length;
        b[1] = 0x02;
        for (var i = 0; i < d.Services.Count; i++) (b[2 + (2 * i)], b[3 + (2 * i)]) = d.Services[i];
        return b;
    }

    /// <summary>Reads the DIBs of a search or description response.</summary>
    public static KnxDeviceInfo ReadDibs(ReadOnlySpan<byte> b, IPEndPoint? control)
    {
        var info = new KnxDeviceInfo { ControlEndpoint = control };
        var services = new List<(byte, byte)>();
        while (b.Length >= 2)
        {
            var len = b[0];
            if (len < 2 || len > b.Length) throw new ProtocolException("DIB length out of range.");
            var dib = b[..len];
            switch (dib[1])
            {
                case 0x01 when len == 54:
                    info = info with
                    {
                        Medium = dib[2],
                        Address = new KnxIndividualAddress(BinaryPrimitives.ReadUInt16BigEndian(dib[4..])),
                        ProjectInstallation = BinaryPrimitives.ReadUInt16BigEndian(dib[6..]),
                        SerialNumber = dib.Slice(8, 6).ToArray(),
                        Multicast = new IPAddress(dib.Slice(14, 4)),
                        Mac = dib.Slice(18, 6).ToArray(),
                        FriendlyName = Encoding.Latin1.GetString(dib.Slice(24, 30)).TrimEnd('\0'),
                    };
                    break;
                case 0x02:
                    for (var i = 2; i + 1 < len; i += 2) services.Add((dib[i], dib[i + 1]));
                    break;
            }

            b = b[len..];
        }

        return info with { Services = services };
    }

    /// <summary>One-line description of a frame.</summary>
    public static string Describe(ReadOnlyMemory<byte> frame)
    {
        try
        {
            var (service, body) = Parse(frame);
            var b = body.Span;
            return service switch
            {
                KnxServiceType.TunnelingRequest => $"TUNNELING_REQUEST ch {b[1]} seq {b[2]}: {KnxCemiFrame.Decode(b[4..])}",
                KnxServiceType.TunnelingAck => $"TUNNELING_ACK ch {b[1]} seq {b[2]} {KnxIpStatus.Name(b[3])}",
                KnxServiceType.RoutingIndication => $"ROUTING_INDICATION: {KnxCemiFrame.Decode(b)}",
                KnxServiceType.ConnectResponse => b[1] == 0 ? $"CONNECT_RESPONSE ch {b[0]} OK, address {ReadConnectResponse(b).Assigned}" : $"CONNECT_RESPONSE {KnxIpStatus.Name(b[1])}",
                KnxServiceType.ConnectionStateRequest or KnxServiceType.DisconnectRequest => $"{Name(service)} ch {b[0]}",
                KnxServiceType.ConnectionStateResponse or KnxServiceType.DisconnectResponse => $"{Name(service)} ch {b[0]} {KnxIpStatus.Name(b[1])}",
                KnxServiceType.SearchResponse => $"SEARCH_RESPONSE {ReadDibs(b[8..], ReadHpai(b)).FriendlyName}",
                _ => Name(service),
            };
        }
        catch (Exception ex) when (ex is ProtocolException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return $"invalid KNXnet/IP frame: {ex.Message}";
        }
    }

    /// <summary>Specification name of a service.</summary>
    public static string Name(KnxServiceType service) => service switch
    {
        KnxServiceType.SearchRequest => "SEARCH_REQUEST",
        KnxServiceType.SearchResponse => "SEARCH_RESPONSE",
        KnxServiceType.DescriptionRequest => "DESCRIPTION_REQUEST",
        KnxServiceType.DescriptionResponse => "DESCRIPTION_RESPONSE",
        KnxServiceType.ConnectRequest => "CONNECT_REQUEST",
        KnxServiceType.ConnectResponse => "CONNECT_RESPONSE",
        KnxServiceType.ConnectionStateRequest => "CONNECTIONSTATE_REQUEST",
        KnxServiceType.ConnectionStateResponse => "CONNECTIONSTATE_RESPONSE",
        KnxServiceType.DisconnectRequest => "DISCONNECT_REQUEST",
        KnxServiceType.DisconnectResponse => "DISCONNECT_RESPONSE",
        KnxServiceType.TunnelingRequest => "TUNNELING_REQUEST",
        KnxServiceType.TunnelingAck => "TUNNELING_ACK",
        KnxServiceType.RoutingIndication => "ROUTING_INDICATION",
        KnxServiceType.RoutingLostMessage => "ROUTING_LOST_MESSAGE",
        KnxServiceType.RoutingBusy => "ROUTING_BUSY",
        _ => $"0x{(ushort)service:X4}",
    };

    /// <summary>Frame lane fields of a KNXnet/IP frame (header, connection header, cEMI).</summary>
    public static IReadOnlyList<FrameField> DescribeFields(ReadOnlySpan<byte> b)
    {
        var fields = new List<FrameField>();
        if (b.Length < HeaderSize)
        {
            fields.Add(new FrameField("Frame", 0, b.Length, FrameFieldKind.Error, "shorter than the header"));
            return fields;
        }

        var service = (KnxServiceType)BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        fields.Add(new FrameField("Header", 0, 2, FrameFieldKind.Header, $"length {b[0]}, version {b[1] >> 4}.{b[1] & 0xF}"));
        fields.Add(new FrameField("Service", 2, 2, FrameFieldKind.Function, Name(service)));
        fields.Add(new FrameField("Total length", 4, 2, FrameFieldKind.Length, BinaryPrimitives.ReadUInt16BigEndian(b[4..]).ToString(CultureInfo.InvariantCulture)));
        var body = b[HeaderSize..];
        switch (service)
        {
            case KnxServiceType.TunnelingRequest when body.Length >= 4:
                fields.Add(new FrameField("Connection header", 6, 4, FrameFieldKind.Address, $"channel {body[1]}, sequence {body[2]}"));
                fields.AddRange(KnxCemiFrame.Describe(body[4..], 10));
                break;
            case KnxServiceType.TunnelingAck when body.Length >= 4:
                fields.Add(new FrameField("Connection header", 6, 4, FrameFieldKind.Address, $"channel {body[1]}, sequence {body[2]}, {KnxIpStatus.Name(body[3])}"));
                break;
            case KnxServiceType.RoutingIndication:
                fields.AddRange(KnxCemiFrame.Describe(body, 6));
                break;
            default:
                if (body.Length > 0) fields.Add(new FrameField("Body", 6, body.Length, FrameFieldKind.Data));
                break;
        }

        return fields;
    }
}
