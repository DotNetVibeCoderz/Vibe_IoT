using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace IoTCom.Net.Protocols.Bacnet;

/// <summary>BVLC functions (Annex J).</summary>
public enum BacnetBvlcFunction : byte
{
    /// <summary>BVLC-Result.</summary>
    Result = 0x00,
    /// <summary>Forwarded-NPDU (from a BBMD, with the original source).</summary>
    ForwardedNpdu = 0x04,
    /// <summary>Register-Foreign-Device.</summary>
    RegisterForeignDevice = 0x05,
    /// <summary>Distribute-Broadcast-To-Network.</summary>
    DistributeBroadcastToNetwork = 0x09,
    /// <summary>Original-Unicast-NPDU.</summary>
    OriginalUnicastNpdu = 0x0A,
    /// <summary>Original-Broadcast-NPDU.</summary>
    OriginalBroadcastNpdu = 0x0B,
}

/// <summary>APDU types (clause 20.1).</summary>
public enum BacnetPduType : byte
{
    /// <summary>BACnet-Confirmed-Request-PDU.</summary>
    ConfirmedRequest = 0,
    /// <summary>BACnet-Unconfirmed-Request-PDU.</summary>
    UnconfirmedRequest = 1,
    /// <summary>BACnet-SimpleACK-PDU.</summary>
    SimpleAck = 2,
    /// <summary>BACnet-ComplexACK-PDU.</summary>
    ComplexAck = 3,
    /// <summary>BACnet-SegmentACK-PDU.</summary>
    SegmentAck = 4,
    /// <summary>BACnet-Error-PDU.</summary>
    Error = 5,
    /// <summary>BACnet-Reject-PDU.</summary>
    Reject = 6,
    /// <summary>BACnet-Abort-PDU.</summary>
    Abort = 7,
}

/// <summary>
/// One BACnet/IP datagram (sans-I/O): BVLC header, NPDU (network addressing is parsed and kept, not routed) and the
/// APDU header with its service data.
/// </summary>
public sealed record BacnetPacket
{
    /// <summary>BVLC function.</summary>
    public BacnetBvlcFunction Function { get; init; } = BacnetBvlcFunction.OriginalUnicastNpdu;

    /// <summary>Original source of a Forwarded-NPDU.</summary>
    public IPEndPoint? ForwardedFrom { get; init; }

    /// <summary>BVLC-Result code (for <see cref="BacnetBvlcFunction.Result"/>).</summary>
    public ushort ResultCode { get; init; }

    /// <summary>NPDU: a reply is expected (confirmed requests).</summary>
    public bool ExpectingReply { get; init; }

    /// <summary>NPDU: this is a network-layer message, not an APDU.</summary>
    public bool NetworkMessage { get; init; }

    /// <summary>NPDU: destination network, when present (0xFFFF = global broadcast).</summary>
    public ushort? DestinationNetwork { get; init; }

    /// <summary>NPDU: source network, when present (routed messages).</summary>
    public ushort? SourceNetwork { get; init; }

    /// <summary>NPDU: source MAC address on the remote network.</summary>
    public byte[]? SourceAddress { get; init; }

    /// <summary>APDU type.</summary>
    public BacnetPduType PduType { get; init; }

    /// <summary>Service choice (confirmed or unconfirmed service number).</summary>
    public byte Service { get; init; }

    /// <summary>Invoke id of confirmed requests and their answers.</summary>
    public byte InvokeId { get; init; }

    /// <summary>Largest APDU the requester accepts (confirmed requests).</summary>
    public int MaxApdu { get; init; } = 1476;

    /// <summary>The requester accepts a segmented response.</summary>
    public bool SegmentedResponseAccepted { get; init; }

    /// <summary>The APDU is a segment of a larger message (not supported beyond detection).</summary>
    public bool Segmented { get; init; }

    /// <summary>Service data after the APDU header.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>Error class (Error-PDU).</summary>
    public BacnetErrorClass ErrorClass { get; init; }

    /// <summary>Error code (Error-PDU).</summary>
    public BacnetErrorCode ErrorCode { get; init; }

    /// <summary>Reject or abort reason.</summary>
    public byte Reason { get; init; }

    private static readonly int[] ApduSizes = [50, 128, 206, 480, 1024, 1476];

    /// <summary>Encodes BVLC + NPDU + APDU.</summary>
    public byte[] Encode()
    {
        var npdu = new List<byte> { 0x01 };
        byte control = 0;
        if (NetworkMessage) control |= 0x80;
        if (DestinationNetwork is not null) control |= 0x20;
        if (ExpectingReply) control |= 0x04;
        npdu.Add(control);
        if (DestinationNetwork is { } dnet)
        {
            npdu.Add((byte)(dnet >> 8));
            npdu.Add((byte)dnet);
            npdu.Add(0);       // DLEN 0 = broadcast on that network
            npdu.Add(255);     // hop count
        }

        var apdu = new List<byte>();
        switch (PduType)
        {
            case BacnetPduType.ConfirmedRequest:
                apdu.Add((byte)(SegmentedResponseAccepted ? 0x02 : 0x00));
                var size = Array.FindIndex(ApduSizes, s => s >= MaxApdu);
                apdu.Add((byte)(size < 0 ? 5 : size));
                apdu.Add(InvokeId);
                apdu.Add(Service);
                break;
            case BacnetPduType.UnconfirmedRequest:
                apdu.Add(0x10);
                apdu.Add(Service);
                break;
            case BacnetPduType.SimpleAck:
                apdu.Add(0x20);
                apdu.Add(InvokeId);
                apdu.Add(Service);
                break;
            case BacnetPduType.ComplexAck:
                apdu.Add(0x30);
                apdu.Add(InvokeId);
                apdu.Add(Service);
                break;
            case BacnetPduType.Error:
                apdu.Add(0x50);
                apdu.Add(InvokeId);
                apdu.Add(Service);
                apdu.AddRange(new BacnetWriter().Application(BacnetValue.Enumerated((uint)ErrorClass)).Application(BacnetValue.Enumerated((uint)ErrorCode)).ToArray());
                break;
            case BacnetPduType.Reject:
                apdu.Add(0x60);
                apdu.Add(InvokeId);
                apdu.Add(Reason);
                break;
            case BacnetPduType.Abort:
                apdu.Add(0x71);       // sent by the server
                apdu.Add(InvokeId);
                apdu.Add(Reason);
                break;
            default:
                throw new NotSupportedException($"Encoding {PduType} is not supported.");
        }

        if (PduType is not (BacnetPduType.Error or BacnetPduType.Reject or BacnetPduType.Abort)) apdu.AddRange(Data);
        var body = NetworkMessage ? npdu.Concat(Data).ToArray() : [.. npdu, .. apdu];
        var head = Function == BacnetBvlcFunction.ForwardedNpdu && ForwardedFrom is { } from ? 10 : 4;
        var b = new byte[head + body.Length];
        b[0] = 0x81;
        b[1] = (byte)Function;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)b.Length);
        if (head == 10)
        {
            ForwardedFrom!.Address.MapToIPv4().GetAddressBytes().CopyTo(b, 4);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), (ushort)ForwardedFrom.Port);
        }

        body.CopyTo(b, head);
        return b;
    }

    /// <summary>Decodes a BACnet/IP datagram.</summary>
    public static BacnetPacket Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) throw new ProtocolException("BACnet/IP datagram shorter than the 4-byte BVLC header.");
        if (b[0] != 0x81) throw new ProtocolException($"BVLC type 0x{b[0]:X2} is not BACnet/IP (0x81).");
        var function = (BacnetBvlcFunction)b[1];
        var length = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
        if (length != b.Length) throw new ProtocolException($"BVLC length {length} does not match the {b.Length} byte(s) received.");
        if (function == BacnetBvlcFunction.Result)
        {
            if (b.Length != 6) throw new ProtocolException("BVLC-Result must be 6 bytes.");
            return new BacnetPacket { Function = function, ResultCode = BinaryPrimitives.ReadUInt16BigEndian(b[4..]) };
        }

        IPEndPoint? forwarded = null;
        var o = 4;
        if (function == BacnetBvlcFunction.ForwardedNpdu)
        {
            if (b.Length < 10) throw new ProtocolException("Forwarded-NPDU too short.");
            forwarded = new IPEndPoint(new IPAddress(b.Slice(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(b[8..]));
            o = 10;
        }
        else if (function is not (BacnetBvlcFunction.OriginalUnicastNpdu or BacnetBvlcFunction.OriginalBroadcastNpdu or BacnetBvlcFunction.DistributeBroadcastToNetwork))
        {
            return new BacnetPacket { Function = function, Data = b[4..].ToArray() };
        }

        // NPDU
        if (b.Length < o + 2) throw new ProtocolException("NPDU too short.");
        if (b[o] != 0x01) throw new ProtocolException($"NPDU version {b[o]} is not 1.");
        var control = b[o + 1];
        o += 2;
        ushort? dnet = null, snet = null;
        byte[]? sadr = null;
        if ((control & 0x20) != 0)
        {
            if (b.Length < o + 3) throw new ProtocolException("NPDU destination truncated.");
            dnet = BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
            var dlen = b[o + 2];
            o += 3 + dlen;
        }

        if ((control & 0x08) != 0)
        {
            if (b.Length < o + 3) throw new ProtocolException("NPDU source truncated.");
            snet = BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
            var slen = b[o + 2];
            if (b.Length < o + 3 + slen) throw new ProtocolException("NPDU source address truncated.");
            sadr = b.Slice(o + 3, slen).ToArray();
            o += 3 + slen;
        }

        if ((control & 0x20) != 0) o++;   // hop count
        var network = (control & 0x80) != 0;
        var packet = new BacnetPacket
        {
            Function = function, ForwardedFrom = forwarded, ExpectingReply = (control & 0x04) != 0, NetworkMessage = network,
            DestinationNetwork = dnet, SourceNetwork = snet, SourceAddress = sadr,
        };
        if (network) return packet with { Data = b[o..].ToArray() };
        if (b.Length <= o) throw new ProtocolException("APDU missing.");

        // APDU
        var a = b[o..];
        var type = (BacnetPduType)(a[0] >> 4);
        try
        {
            switch (type)
            {
                case BacnetPduType.ConfirmedRequest:
                {
                    var seg = (a[0] & 0x08) != 0;
                    var h = seg ? 6 : 4;
                    var size = a[1] & 0x0F;
                    return packet with
                    {
                        PduType = type, Segmented = seg, SegmentedResponseAccepted = (a[0] & 0x02) != 0, MaxApdu = size < ApduSizes.Length ? ApduSizes[size] : 1476,
                        InvokeId = a[2], Service = a[h - 1], Data = a[h..].ToArray(),
                    };
                }

                case BacnetPduType.UnconfirmedRequest:
                    return packet with { PduType = type, Service = a[1], Data = a[2..].ToArray() };
                case BacnetPduType.SimpleAck:
                    return packet with { PduType = type, InvokeId = a[1], Service = a[2] };
                case BacnetPduType.ComplexAck:
                {
                    var seg = (a[0] & 0x08) != 0;
                    var h = seg ? 5 : 3;
                    return packet with { PduType = type, Segmented = seg, InvokeId = a[1], Service = a[h - 1], Data = a[h..].ToArray() };
                }

                case BacnetPduType.Error:
                {
                    var r = new BacnetReader(a[3..]);
                    if (r.IsOpening(0)) r.Open(0);   // some services wrap the error in context tag 0
                    var cls = (BacnetErrorClass)(uint)r.Application().AsDouble()!;
                    var code = (BacnetErrorCode)(uint)r.Application().AsDouble()!;
                    return packet with { PduType = type, InvokeId = a[1], Service = a[2], ErrorClass = cls, ErrorCode = code };
                }

                case BacnetPduType.Reject:
                case BacnetPduType.Abort:
                    return packet with { PduType = type, InvokeId = a[1], Reason = a[2] };
                case BacnetPduType.SegmentAck:
                    return packet with { PduType = type, InvokeId = a[1] };
                default:
                    throw new ProtocolException($"APDU type {(int)type} is not defined.");
            }
        }
        catch (IndexOutOfRangeException)
        {
            throw new ProtocolException($"{type} APDU is truncated.");
        }
    }

    /// <summary>Service name.</summary>
    public string ServiceName => PduType switch
    {
        BacnetPduType.UnconfirmedRequest => ((BacnetUnconfirmedService)Service).ToString(),
        BacnetPduType.Reject or BacnetPduType.Abort or BacnetPduType.SegmentAck => "",
        _ => ((BacnetConfirmedService)Service).ToString(),
    };

    /// <summary>One-line description.</summary>
    public string Describe() => Function switch
    {
        BacnetBvlcFunction.Result => $"BVLC-Result 0x{ResultCode:X4}",
        _ when NetworkMessage => "network-layer message",
        _ => PduType switch
        {
            BacnetPduType.ConfirmedRequest => $"{ServiceName} request #{InvokeId}",
            BacnetPduType.UnconfirmedRequest => ServiceName,
            BacnetPduType.SimpleAck => $"{ServiceName} SimpleACK #{InvokeId}",
            BacnetPduType.ComplexAck => $"{ServiceName} ComplexACK #{InvokeId}",
            BacnetPduType.Error => $"{ServiceName} Error #{InvokeId}: {ErrorClass}/{ErrorCode}",
            BacnetPduType.Reject => $"Reject #{InvokeId}: {(BacnetRejectReason)Reason}",
            BacnetPduType.Abort => $"Abort #{InvokeId}: {(BacnetAbortReason)Reason}",
            _ => PduType.ToString(),
        },
    };

    /// <summary>Frame lane fields.</summary>
    public static IReadOnlyList<FrameField> DescribeFields(ReadOnlySpan<byte> b)
    {
        var fields = new List<FrameField>();
        BacnetPacket p;
        try
        {
            p = Decode(b);
        }
        catch (ProtocolException ex)
        {
            fields.Add(new FrameField("Invalid", 0, b.Length, FrameFieldKind.Error, ex.Message));
            return fields;
        }

        fields.Add(new FrameField("BVLC", 0, 4, FrameFieldKind.Header, p.Function.ToString()));
        if (p.Function == BacnetBvlcFunction.Result)
        {
            fields.Add(new FrameField("Result", 4, 2, FrameFieldKind.Data, $"0x{p.ResultCode:X4}"));
            return fields;
        }

        var o = 4;
        if (p.ForwardedFrom is not null)
        {
            fields.Add(new FrameField("Original source", 4, 6, FrameFieldKind.Address, p.ForwardedFrom.ToString()));
            o = 10;
        }

        var apduStart = b.Length - (p.PduType switch
        {
            BacnetPduType.ConfirmedRequest => 4 + p.Data.Length,
            BacnetPduType.UnconfirmedRequest => 2 + p.Data.Length,
            BacnetPduType.SimpleAck => 3,
            BacnetPduType.ComplexAck => 3 + p.Data.Length,
            _ => b.Length - o - 2 > 0 ? b.Length - o - 2 : 0,
        });
        fields.Add(new FrameField("NPDU", o, Math.Max(1, apduStart - o), FrameFieldKind.Header, p.ExpectingReply ? "expecting reply" : "no reply"));
        if (p.NetworkMessage) return fields;
        var header = p.PduType switch { BacnetPduType.ConfirmedRequest => 4, BacnetPduType.UnconfirmedRequest => 2, _ => 3 };
        fields.Add(new FrameField("APDU", apduStart, Math.Min(header, b.Length - apduStart), FrameFieldKind.Function, p.Describe()));
        if (p.Data.Length > 0) fields.Add(new FrameField("Service data", apduStart + header, p.Data.Length, FrameFieldKind.Data, Convert.ToHexString(p.Data)));
        else if (b.Length > apduStart + header) fields.Add(new FrameField(p.PduType == BacnetPduType.Error ? "Error" : "Reason", apduStart + header, b.Length - apduStart - header, FrameFieldKind.Error, p.Describe()));
        return fields;
    }

    /// <inheritdoc />
    public override string ToString() => Describe() + (Data.Length > 0 ? $" [{Data.Length.ToString(CultureInfo.InvariantCulture)} B]" : "");
}
