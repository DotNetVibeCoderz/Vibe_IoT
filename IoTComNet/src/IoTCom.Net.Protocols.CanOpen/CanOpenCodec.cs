using System.Buffers.Binary;
using System.Globalization;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Protocols.CanOpen;

/// <summary>Communication objects of the CiA 301 predefined connection set.</summary>
public enum CanOpenFunction
{
    /// <summary>NMT command (COB-ID 0x000).</summary>
    Nmt,
    /// <summary>SYNC (0x080).</summary>
    Sync,
    /// <summary>Emergency (0x080 + node).</summary>
    Emergency,
    /// <summary>Time stamp (0x100).</summary>
    Time,
    /// <summary>Transmit PDO 1–4 (0x180/0x280/0x380/0x480 + node).</summary>
    Tpdo,
    /// <summary>Receive PDO 1–4 (0x200/0x300/0x400/0x500 + node).</summary>
    Rpdo,
    /// <summary>SDO server → client (0x580 + node).</summary>
    SdoResponse,
    /// <summary>SDO client → server (0x600 + node).</summary>
    SdoRequest,
    /// <summary>Heartbeat / boot-up / node guarding (0x700 + node).</summary>
    Heartbeat,
    /// <summary>LSS (0x7E4/0x7E5).</summary>
    Lss,
    /// <summary>Not part of the predefined set.</summary>
    Other,
}

/// <summary>NMT commands.</summary>
public enum NmtCommand : byte
{
    /// <summary>Start remote node (→ operational).</summary>
    Start = 0x01,
    /// <summary>Stop remote node (→ stopped).</summary>
    Stop = 0x02,
    /// <summary>Enter pre-operational.</summary>
    EnterPreOperational = 0x80,
    /// <summary>Reset node (application and communication).</summary>
    ResetNode = 0x81,
    /// <summary>Reset communication.</summary>
    ResetCommunication = 0x82,
}

/// <summary>NMT states as reported in heartbeat messages.</summary>
public enum NmtState : byte
{
    /// <summary>Boot-up message (initialisation finished).</summary>
    BootUp = 0x00,
    /// <summary>Stopped.</summary>
    Stopped = 0x04,
    /// <summary>Operational.</summary>
    Operational = 0x05,
    /// <summary>Pre-operational.</summary>
    PreOperational = 0x7F,
}

/// <summary>Kinds of SDO frames.</summary>
public enum SdoKind
{
    /// <summary>Client: initiate upload (read).</summary>
    InitiateUploadRequest,
    /// <summary>Server: initiate upload response (expedited data, or the size of a segmented transfer).</summary>
    InitiateUploadResponse,
    /// <summary>Client: upload segment request.</summary>
    UploadSegmentRequest,
    /// <summary>Server: upload segment response.</summary>
    UploadSegmentResponse,
    /// <summary>Client: initiate download (write), expedited data or size.</summary>
    InitiateDownloadRequest,
    /// <summary>Server: initiate download response.</summary>
    InitiateDownloadResponse,
    /// <summary>Client: download segment.</summary>
    DownloadSegmentRequest,
    /// <summary>Server: download segment response.</summary>
    DownloadSegmentResponse,
    /// <summary>Either side: abort transfer.</summary>
    Abort,
}

/// <summary>A decoded SDO frame (always 8 bytes on the wire).</summary>
public sealed record SdoFrame
{
    /// <summary>Kind.</summary>
    public required SdoKind Kind { get; init; }

    /// <summary>Object index (initiate and abort frames).</summary>
    public ushort Index { get; init; }

    /// <summary>Sub-index (initiate and abort frames).</summary>
    public byte SubIndex { get; init; }

    /// <summary>Expedited transfer (data in the initiate frame).</summary>
    public bool Expedited { get; init; }

    /// <summary>Size indicated (expedited: data length known; segmented: <see cref="Size"/> valid).</summary>
    public bool SizeIndicated { get; init; }

    /// <summary>Total size of a segmented transfer.</summary>
    public uint Size { get; init; }

    /// <summary>Toggle bit of segment frames.</summary>
    public bool Toggle { get; init; }

    /// <summary>Last segment.</summary>
    public bool Last { get; init; }

    /// <summary>Data carried by this frame (expedited data or segment data).</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>Abort code.</summary>
    public uint AbortCode { get; init; }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        SdoKind.Abort => $"abort {Index:X4}:{SubIndex:X2} 0x{AbortCode:X8} {SdoAbort.Describe(AbortCode)}",
        SdoKind.InitiateUploadRequest => $"read {Index:X4}:{SubIndex:X2}",
        SdoKind.InitiateDownloadResponse => $"write ok {Index:X4}:{SubIndex:X2}",
        SdoKind.InitiateUploadResponse or SdoKind.InitiateDownloadRequest => Expedited
            ? $"{(Kind == SdoKind.InitiateUploadResponse ? "value" : "write")} {Index:X4}:{SubIndex:X2} = {Convert.ToHexString(Data)}"
            : $"{(Kind == SdoKind.InitiateUploadResponse ? "value" : "write")} {Index:X4}:{SubIndex:X2} segmented, {Size} bytes",
        _ => $"{Kind} t={(Toggle ? 1 : 0)}{(Last ? " last" : "")} {Convert.ToHexString(Data)}",
    };
}

/// <summary>SDO abort codes (CiA 301, table 22).</summary>
public static class SdoAbort
{
    /// <summary>Toggle bit not alternated.</summary>
    public const uint ToggleBit = 0x0503_0000;
    /// <summary>SDO protocol timed out.</summary>
    public const uint Timeout = 0x0504_0000;
    /// <summary>Client/server command specifier not valid.</summary>
    public const uint InvalidCommand = 0x0504_0001;
    /// <summary>Unsupported access to an object.</summary>
    public const uint UnsupportedAccess = 0x0601_0000;
    /// <summary>Attempt to read a write-only object.</summary>
    public const uint WriteOnly = 0x0601_0001;
    /// <summary>Attempt to write a read-only object.</summary>
    public const uint ReadOnly = 0x0601_0002;
    /// <summary>Object does not exist in the object dictionary.</summary>
    public const uint NoObject = 0x0602_0000;
    /// <summary>Data type does not match, length of service parameter does not match.</summary>
    public const uint LengthMismatch = 0x0607_0010;
    /// <summary>Sub-index does not exist.</summary>
    public const uint NoSubIndex = 0x0609_0011;
    /// <summary>Value range of parameter exceeded.</summary>
    public const uint ValueRange = 0x0609_0030;
    /// <summary>General error.</summary>
    public const uint General = 0x0800_0000;
    /// <summary>Data cannot be transferred or stored because of the present device state.</summary>
    public const uint DeviceState = 0x0800_0022;

    /// <summary>A short description of an abort code.</summary>
    public static string Describe(uint code) => code switch
    {
        ToggleBit => "toggle bit not alternated",
        Timeout => "SDO protocol timed out",
        InvalidCommand => "invalid command specifier",
        UnsupportedAccess => "unsupported access",
        WriteOnly => "object is write-only",
        ReadOnly => "object is read-only",
        NoObject => "object does not exist",
        LengthMismatch => "length does not match",
        NoSubIndex => "sub-index does not exist",
        ValueRange => "value range exceeded",
        General => "general error",
        DeviceState => "not possible in the present device state",
        _ => string.Create(CultureInfo.InvariantCulture, $"abort 0x{code:X8}"),
    };
}

/// <summary>An emergency message (EMCY).</summary>
/// <param name="ErrorCode">CiA 301 error code (0x0000 = error reset).</param>
/// <param name="ErrorRegister">Object 0x1001.</param>
/// <param name="Manufacturer">5 manufacturer-specific bytes.</param>
public sealed record CanOpenEmergency(ushort ErrorCode, byte ErrorRegister, byte[] Manufacturer)
{
    /// <summary>Decodes 8 data bytes.</summary>
    public static CanOpenEmergency Parse(ReadOnlySpan<byte> d) =>
        d.Length < 8 ? throw new FormatException("EMCY carries 8 bytes.") : new(BinaryPrimitives.ReadUInt16LittleEndian(d), d[2], d[3..8].ToArray());

    /// <summary>Encodes 8 data bytes.</summary>
    public byte[] Encode()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, ErrorCode);
        b[2] = ErrorRegister;
        Manufacturer.AsSpan(0, Math.Min(5, Manufacturer.Length)).CopyTo(b.AsSpan(3));
        return b;
    }

    /// <summary>Error class from the high byte of the code.</summary>
    public string Description => (ErrorCode >> 8) switch
    {
        0x00 => "error reset / no error",
        0x10 => "generic error",
        0x20 or 0x21 or 0x22 or 0x23 => "current",
        0x30 or 0x31 or 0x32 or 0x33 => "voltage",
        0x40 or 0x41 or 0x42 => "temperature",
        0x50 => "device hardware",
        0x60 or 0x61 or 0x62 or 0x63 => "device software",
        0x70 => "additional modules",
        0x80 or 0x81 or 0x82 => "monitoring / communication",
        0x90 => "external error",
        0xF0 => "additional functions",
        0xFF => "device specific",
        _ => "unknown",
    };

    /// <inheritdoc />
    public override string ToString() => $"EMCY 0x{ErrorCode:X4} ({Description}) register 0x{ErrorRegister:X2}";
}

/// <summary>COB-IDs, NMT, heartbeat and SDO frame encoding (CiA 301).</summary>
public static class CanOpenCodec
{
    /// <summary>The function and node id of a COB-ID in the predefined connection set.</summary>
    public static (CanOpenFunction Function, byte Node, int PdoNumber) Classify(uint cobId)
    {
        if (cobId == 0x000) return (CanOpenFunction.Nmt, 0, 0);
        if (cobId == 0x080) return (CanOpenFunction.Sync, 0, 0);
        if (cobId == 0x100) return (CanOpenFunction.Time, 0, 0);
        if (cobId is 0x7E4 or 0x7E5) return (CanOpenFunction.Lss, 0, 0);
        if (cobId > 0x7FF) return (CanOpenFunction.Other, 0, 0);
        var node = (byte)(cobId & 0x7F);
        if (node == 0) return (CanOpenFunction.Other, 0, 0);
        return (cobId & 0x780) switch
        {
            0x080 => (CanOpenFunction.Emergency, node, 0),
            0x180 => (CanOpenFunction.Tpdo, node, 1),
            0x200 => (CanOpenFunction.Rpdo, node, 1),
            0x280 => (CanOpenFunction.Tpdo, node, 2),
            0x300 => (CanOpenFunction.Rpdo, node, 2),
            0x380 => (CanOpenFunction.Tpdo, node, 3),
            0x400 => (CanOpenFunction.Rpdo, node, 3),
            0x480 => (CanOpenFunction.Tpdo, node, 4),
            0x500 => (CanOpenFunction.Rpdo, node, 4),
            0x580 => (CanOpenFunction.SdoResponse, node, 0),
            0x600 => (CanOpenFunction.SdoRequest, node, 0),
            0x700 => (CanOpenFunction.Heartbeat, node, 0),
            _ => (CanOpenFunction.Other, 0, 0),
        };
    }

    /// <summary>Default COB-ID of TPDO <paramref name="n"/> (1–4) of a node.</summary>
    public static uint TpdoCobId(int n, byte node) => (uint)(0x180 + ((n - 1) * 0x100) + node);

    /// <summary>Default COB-ID of RPDO <paramref name="n"/> (1–4) of a node.</summary>
    public static uint RpdoCobId(int n, byte node) => (uint)(0x200 + ((n - 1) * 0x100) + node);

    /// <summary>An NMT command frame (<paramref name="node"/> 0 = all nodes).</summary>
    public static CanFrame Nmt(NmtCommand command, byte node) => new(0x000, new[] { (byte)command, node });

    /// <summary>A heartbeat (or boot-up) frame.</summary>
    public static CanFrame Heartbeat(byte node, NmtState state) => new(0x700u + node, new[] { (byte)state });

    /// <summary>Encodes an SDO frame as its 8 data bytes.</summary>
    public static byte[] EncodeSdo(SdoFrame f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var b = new byte[8];
        void Mux()
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), f.Index);
            b[3] = f.SubIndex;
        }

        switch (f.Kind)
        {
            case SdoKind.InitiateUploadRequest:
                b[0] = 0x40;
                Mux();
                break;
            case SdoKind.InitiateUploadResponse or SdoKind.InitiateDownloadRequest:
                var ccs = f.Kind == SdoKind.InitiateUploadResponse ? 0x40 : 0x20;
                if (f.Expedited)
                {
                    if (f.Data.Length is < 1 or > 4) throw new ArgumentException("Expedited SDO data is 1–4 bytes.");
                    b[0] = (byte)(ccs | ((4 - f.Data.Length) << 2) | 0x03);
                    f.Data.CopyTo(b, 4);
                }
                else
                {
                    b[0] = (byte)(ccs | (f.SizeIndicated ? 0x01 : 0));
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), f.Size);
                }

                Mux();
                break;
            case SdoKind.InitiateDownloadResponse:
                b[0] = 0x60;
                Mux();
                break;
            case SdoKind.UploadSegmentRequest:
                b[0] = (byte)(0x60 | (f.Toggle ? 0x10 : 0));
                break;
            case SdoKind.DownloadSegmentResponse:
                b[0] = (byte)(0x20 | (f.Toggle ? 0x10 : 0));
                break;
            case SdoKind.UploadSegmentResponse or SdoKind.DownloadSegmentRequest:
                if (f.Data.Length > 7) throw new ArgumentException("An SDO segment carries at most 7 bytes.");
                b[0] = (byte)((f.Toggle ? 0x10 : 0) | ((7 - f.Data.Length) << 1) | (f.Last ? 0x01 : 0));
                f.Data.CopyTo(b, 1);
                break;
            case SdoKind.Abort:
                b[0] = 0x80;
                Mux();
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), f.AbortCode);
                break;
        }

        return b;
    }

    /// <summary>
    /// Decodes SDO data bytes. <paramref name="fromServer"/> selects the direction (0x580 vs 0x600 COB-IDs), because
    /// segment frames of both directions share command specifier values.
    /// </summary>
    public static SdoFrame DecodeSdo(ReadOnlySpan<byte> d, bool fromServer)
    {
        if (d.Length < 8) throw new FormatException($"An SDO frame has 8 data bytes, got {d.Length}.");
        var cmd = d[0];
        var index = BinaryPrimitives.ReadUInt16LittleEndian(d[1..]);
        var sub = d[3];
        var cs = cmd >> 5;
        if (cs == 4) return new SdoFrame { Kind = SdoKind.Abort, Index = index, SubIndex = sub, AbortCode = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]) };
        var raw = d.ToArray();
        bool toggle = (cmd & 0x10) != 0, last = (cmd & 0x01) != 0;
        SdoFrame Segment(SdoKind kind) => new() { Kind = kind, Toggle = toggle, Last = last, Data = raw.AsSpan(1, 7 - ((cmd >> 1) & 0x07)).ToArray() };
        SdoFrame Initiate(SdoKind kind)
        {
            bool e = (cmd & 0x02) != 0, s = (cmd & 0x01) != 0;
            if (e)
            {
                var n = s ? 4 - ((cmd >> 2) & 0x03) : 4;
                return new SdoFrame { Kind = kind, Index = index, SubIndex = sub, Expedited = true, SizeIndicated = s, Data = raw.AsSpan(4, n).ToArray() };
            }

            return new SdoFrame { Kind = kind, Index = index, SubIndex = sub, SizeIndicated = s, Size = s ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)) : 0 };
        }

        return (fromServer, cs) switch
        {
            (false, 2) => new SdoFrame { Kind = SdoKind.InitiateUploadRequest, Index = index, SubIndex = sub },
            (false, 1) => Initiate(SdoKind.InitiateDownloadRequest),
            (false, 3) => new SdoFrame { Kind = SdoKind.UploadSegmentRequest, Toggle = toggle },
            (false, 0) => Segment(SdoKind.DownloadSegmentRequest),
            (true, 2) => Initiate(SdoKind.InitiateUploadResponse),
            (true, 3) => new SdoFrame { Kind = SdoKind.InitiateDownloadResponse, Index = index, SubIndex = sub },
            (true, 0) => Segment(SdoKind.UploadSegmentResponse),
            (true, 1) => new SdoFrame { Kind = SdoKind.DownloadSegmentResponse, Toggle = toggle },
            _ => throw new FormatException($"SDO command specifier 0x{cmd:X2} is not supported (block transfer?)."),
        };
    }

    /// <summary>
    /// Frame-lane fields of a CANopen frame in the traffic-tap layout (SocketCAN: 4-byte identifier, length, flags,
    /// data from offset 8): COB-ID meaning plus SDO, NMT, heartbeat or EMCY fields.
    /// </summary>
    public static IReadOnlyList<FrameField> Describe(in CanFrame frame)
    {
        var (function, node, pdo) = Classify(frame.Id);
        var d = frame.Data.Span;
        var fields = new List<FrameField>
        {
            new("COB-ID", 0, 4, FrameFieldKind.Address, function switch
            {
                CanOpenFunction.Tpdo or CanOpenFunction.Rpdo => $"{function}{pdo} node {node}",
                CanOpenFunction.Nmt or CanOpenFunction.Sync or CanOpenFunction.Time or CanOpenFunction.Lss => function.ToString().ToUpperInvariant(),
                _ => $"{function} node {node}",
            }),
            new("Len", 4, 1, FrameFieldKind.Length, d.Length.ToString(CultureInfo.InvariantCulture)),
        };
        try
        {
            switch (function)
            {
                case CanOpenFunction.Nmt when d.Length >= 2:
                    fields.Add(new FrameField("Command", 8, 1, FrameFieldKind.Function, ((NmtCommand)d[0]).ToString()));
                    fields.Add(new FrameField("Node", 9, 1, FrameFieldKind.Address, d[1] == 0 ? "all" : d[1].ToString(CultureInfo.InvariantCulture)));
                    break;
                case CanOpenFunction.Heartbeat when d.Length >= 1:
                    fields.Add(new FrameField("State", 8, 1, FrameFieldKind.Function, ((NmtState)(d[0] & 0x7F)).ToString()));
                    break;
                case CanOpenFunction.SdoRequest or CanOpenFunction.SdoResponse:
                    var sdo = DecodeSdo(d, function == CanOpenFunction.SdoResponse);
                    fields.Add(new FrameField("Command", 8, 1, sdo.Kind == SdoKind.Abort ? FrameFieldKind.Error : FrameFieldKind.Function, sdo.Kind.ToString()));
                    if (sdo.Kind is SdoKind.UploadSegmentRequest or SdoKind.UploadSegmentResponse or SdoKind.DownloadSegmentRequest or SdoKind.DownloadSegmentResponse)
                    {
                        fields.Add(new FrameField("Segment", 9, 7, FrameFieldKind.Data, Convert.ToHexString(sdo.Data)));
                    }
                    else
                    {
                        fields.Add(new FrameField("Index", 9, 3, FrameFieldKind.Header, $"{sdo.Index:X4}:{sdo.SubIndex:X2}"));
                        fields.Add(new FrameField(sdo.Kind == SdoKind.Abort ? "Abort" : "Data", 12, 4, sdo.Kind == SdoKind.Abort ? FrameFieldKind.Error : FrameFieldKind.Data,
                            sdo.Kind == SdoKind.Abort ? SdoAbort.Describe(sdo.AbortCode) : sdo.Expedited ? Convert.ToHexString(sdo.Data) : sdo.SizeIndicated ? $"{sdo.Size} bytes" : ""));
                    }

                    break;
                case CanOpenFunction.Emergency when d.Length >= 8:
                    var e = CanOpenEmergency.Parse(d);
                    fields.Add(new FrameField("Error", 8, 2, FrameFieldKind.Error, $"0x{e.ErrorCode:X4} {e.Description}"));
                    fields.Add(new FrameField("Register", 10, 1, FrameFieldKind.Header, $"0x{e.ErrorRegister:X2}"));
                    fields.Add(new FrameField("Vendor", 11, 5, FrameFieldKind.Data, Convert.ToHexString(e.Manufacturer)));
                    break;
                default:
                    if (d.Length > 0) fields.Add(new FrameField("Data", 8, d.Length, FrameFieldKind.Data, Convert.ToHexString(d)));
                    break;
            }
        }
        catch (FormatException ex)
        {
            fields.Add(new FrameField("Invalid", 8, d.Length, FrameFieldKind.Error, ex.Message));
        }

        return fields;
    }
}
