using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>Result of an attribute access (IEC 62056-5-3 Data-Access-Result).</summary>
public enum DataAccessResult : byte
{
    /// <summary>Success.</summary>
    Success = 0,
    /// <summary>Hardware fault.</summary>
    HardwareFault = 1,
    /// <summary>Temporary failure.</summary>
    TemporaryFailure = 2,
    /// <summary>Read or write denied.</summary>
    ReadWriteDenied = 3,
    /// <summary>Object undefined.</summary>
    ObjectUndefined = 4,
    /// <summary>Object class inconsistent.</summary>
    ObjectClassInconsistent = 9,
    /// <summary>Object unavailable.</summary>
    ObjectUnavailable = 11,
    /// <summary>Type unmatched.</summary>
    TypeUnmatched = 12,
    /// <summary>Scope of access violated.</summary>
    ScopeOfAccessViolated = 13,
    /// <summary>Data block unavailable.</summary>
    DataBlockUnavailable = 14,
    /// <summary>Long get aborted.</summary>
    LongGetAborted = 15,
    /// <summary>No long get in progress.</summary>
    NoLongGetInProgress = 16,
    /// <summary>Data block number invalid.</summary>
    DataBlockNumberInvalid = 19,
    /// <summary>Other reason.</summary>
    OtherReason = 250,
}

/// <summary>Result of a method invocation (Action-Result).</summary>
public enum ActionResult : byte
{
    /// <summary>Success.</summary>
    Success = 0,
    /// <summary>Hardware fault.</summary>
    HardwareFault = 1,
    /// <summary>Temporary failure.</summary>
    TemporaryFailure = 2,
    /// <summary>Read or write denied.</summary>
    ReadWriteDenied = 3,
    /// <summary>Object undefined.</summary>
    ObjectUndefined = 4,
    /// <summary>Object class inconsistent.</summary>
    ObjectClassInconsistent = 9,
    /// <summary>Object unavailable.</summary>
    ObjectUnavailable = 11,
    /// <summary>Type unmatched.</summary>
    TypeUnmatched = 12,
    /// <summary>Scope of access violated.</summary>
    ScopeOfAccessViolated = 13,
    /// <summary>Other reason.</summary>
    OtherReason = 250,
}

/// <summary>Authentication mechanism of an association.</summary>
public enum DlmsAuthentication
{
    /// <summary>No authentication (public client).</summary>
    None = 0,
    /// <summary>Low-level security: a password.</summary>
    Low = 1,
    /// <summary>High-level security, mechanism 5: GMAC challenge-response (security suite 0).</summary>
    HighGmac = 5,
}

/// <summary>xDLMS conformance block bits (24-bit, IEC 62056-5-3).</summary>
[Flags]
public enum DlmsConformance : uint
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>action.</summary>
    Action = 1 << 0,
    /// <summary>event-notification.</summary>
    EventNotification = 1 << 1,
    /// <summary>selective-access.</summary>
    SelectiveAccess = 1 << 2,
    /// <summary>set.</summary>
    Set = 1 << 3,
    /// <summary>get.</summary>
    Get = 1 << 4,
    /// <summary>multiple-references.</summary>
    MultipleReferences = 1 << 9,
    /// <summary>block-transfer-with-action.</summary>
    BlockTransferWithAction = 1 << 10,
    /// <summary>block-transfer-with-set-or-write.</summary>
    BlockTransferWithSet = 1 << 11,
    /// <summary>block-transfer-with-get-or-read.</summary>
    BlockTransferWithGet = 1 << 12,
    /// <summary>attribute0-supported-with-get.</summary>
    Attribute0WithGet = 1 << 13,
    /// <summary>priority-mgmt-supported.</summary>
    PriorityManagement = 1 << 14,
    /// <summary>What a typical LN client proposes (0x007E1F).</summary>
    DefaultClient = Action | EventNotification | SelectiveAccess | Set | Get | MultipleReferences | BlockTransferWithAction
        | BlockTransferWithSet | BlockTransferWithGet | Attribute0WithGet | PriorityManagement,
}

/// <summary>An attribute or method reference: class, logical name, index.</summary>
/// <param name="ClassId">COSEM interface class.</param>
/// <param name="LogicalName">OBIS code.</param>
/// <param name="Index">Attribute or method index.</param>
public readonly record struct CosemReference(ushort ClassId, ObisCode LogicalName, sbyte Index)
{
    internal void Write(List<byte> b)
    {
        b.Add((byte)(ClassId >> 8));
        b.Add((byte)ClassId);
        b.AddRange(LogicalName.ToBytes());
        b.Add(unchecked((byte)Index));
    }

    internal static CosemReference Read(ReadOnlySpan<byte> d, ref int pos)
    {
        if (pos + 9 > d.Length) throw new FormatException("Truncated COSEM attribute descriptor.");
        var r = new CosemReference(BinaryPrimitives.ReadUInt16BigEndian(d[pos..]), ObisCode.FromBytes(d.Slice(pos + 2, 6)), unchecked((sbyte)d[pos + 8]));
        pos += 9;
        return r;
    }

    /// <inheritdoc />
    public override string ToString() => $"{CosemClass.Name(ClassId)} {LogicalName} #{Index}";
}

/// <summary>Selective access: selector and parameters (e.g. range by date for a profile).</summary>
/// <param name="Selector">Access selector (1 = range, 2 = entry).</param>
/// <param name="Parameters">Selector parameters.</param>
public sealed record CosemAccess(byte Selector, CosemData Parameters);

/// <summary>Base of decoded xDLMS and ACSE APDUs.</summary>
public abstract record DlmsPdu
{
    /// <summary>Invoke id and priority (0 for ACSE PDUs).</summary>
    public byte InvokeId { get; init; } = 0xC1;
}

/// <summary>AARQ (association request).</summary>
public sealed record AarqPdu : DlmsPdu
{
    /// <summary>Ciphered application context (LN with ciphering).</summary>
    public bool Ciphered { get; init; }
    /// <summary>Authentication mechanism.</summary>
    public DlmsAuthentication Authentication { get; init; }
    /// <summary>LLS password or HLS challenge (CtoS).</summary>
    public byte[]? AuthenticationValue { get; init; }
    /// <summary>Client system title (ciphering, HLS).</summary>
    public byte[]? SystemTitle { get; init; }
    /// <summary>Proposed conformance.</summary>
    public DlmsConformance Conformance { get; init; } = DlmsConformance.DefaultClient;
    /// <summary>Maximum PDU the client accepts.</summary>
    public ushort MaxPduSize { get; init; } = 0xFFFF;
    /// <summary>Ciphered InitiateRequest (glo-initiateRequest) when <see cref="Ciphered"/>; set by the client.</summary>
    public byte[]? CipheredInitiate { get; init; }
}

/// <summary>AARE (association response).</summary>
public sealed record AarePdu : DlmsPdu
{
    /// <summary>0 accepted, 1 rejected-permanent, 2 rejected-transient.</summary>
    public byte Result { get; init; }
    /// <summary>acse-service-user diagnostic (13 authentication-failure, 14 authentication-required, …).</summary>
    public byte Diagnostic { get; init; }
    /// <summary>Ciphered application context.</summary>
    public bool Ciphered { get; init; }
    /// <summary>Mechanism.</summary>
    public DlmsAuthentication Authentication { get; init; }
    /// <summary>Server system title.</summary>
    public byte[]? SystemTitle { get; init; }
    /// <summary>HLS challenge from the server (StoC).</summary>
    public byte[]? AuthenticationValue { get; init; }
    /// <summary>Negotiated conformance.</summary>
    public DlmsConformance Conformance { get; init; }
    /// <summary>Maximum PDU the server accepts.</summary>
    public ushort MaxPduSize { get; init; } = 0x400;
    /// <summary>Ciphered InitiateResponse (glo-initiateResponse) when <see cref="Ciphered"/>.</summary>
    public byte[]? CipheredInitiate { get; init; }
    /// <summary>Accepted.</summary>
    public bool Accepted => Result == 0;
}

/// <summary>RLRQ / RLRE.</summary>
/// <param name="Response">RLRE when true.</param>
public sealed record ReleasePdu(bool Response) : DlmsPdu;

/// <summary>GET.request normal (one attribute).</summary>
/// <param name="Attribute">Attribute reference.</param>
/// <param name="Access">Selective access.</param>
public sealed record GetRequestPdu(CosemReference Attribute, CosemAccess? Access = null) : DlmsPdu;

/// <summary>GET.request next (block transfer).</summary>
/// <param name="BlockNumber">Number of the last block received.</param>
public sealed record GetNextPdu(uint BlockNumber) : DlmsPdu;

/// <summary>GET.response: data, an error, or one block of a long result.</summary>
public sealed record GetResponsePdu : DlmsPdu
{
    /// <summary>The value (normal response).</summary>
    public CosemData? Data { get; init; }
    /// <summary>The error (normal or block response).</summary>
    public DataAccessResult Result { get; init; }
    /// <summary>Block response: raw block bytes.</summary>
    public byte[]? Block { get; init; }
    /// <summary>Block response: block number.</summary>
    public uint BlockNumber { get; init; }
    /// <summary>Block response: last block.</summary>
    public bool LastBlock { get; init; }
    /// <summary>A with-datablock response (block transfer).</summary>
    public bool IsBlock { get; init; }
}

/// <summary>SET.request normal.</summary>
/// <param name="Attribute">Attribute reference.</param>
/// <param name="Value">New value.</param>
public sealed record SetRequestPdu(CosemReference Attribute, CosemData Value) : DlmsPdu;

/// <summary>SET.response normal.</summary>
/// <param name="Result">Result.</param>
public sealed record SetResponsePdu(DataAccessResult Result) : DlmsPdu;

/// <summary>ACTION.request normal.</summary>
/// <param name="Method">Method reference.</param>
/// <param name="Parameter">Method parameter.</param>
public sealed record ActionRequestPdu(CosemReference Method, CosemData? Parameter) : DlmsPdu;

/// <summary>ACTION.response normal.</summary>
/// <param name="Result">Result.</param>
/// <param name="ReturnValue">Return parameters.</param>
public sealed record ActionResponsePdu(ActionResult Result, CosemData? ReturnValue = null) : DlmsPdu;

/// <summary>Exception-response or confirmed-service-error.</summary>
/// <param name="Description">What the meter reported.</param>
public sealed record ErrorPdu(string Description) : DlmsPdu;

/// <summary>A ciphered APDU (glo-…): tag, security control, invocation counter, ciphertext and tag.</summary>
/// <param name="Tag">glo-tag (e.g. 0xC8 glo-get-request).</param>
/// <param name="SecurityControl">0x30 = authenticated and encrypted, suite 0.</param>
/// <param name="InvocationCounter">Frame counter of the sender.</param>
/// <param name="Payload">Ciphertext (and authentication tag).</param>
public sealed record CipheredPdu(byte Tag, byte SecurityControl, uint InvocationCounter, byte[] Payload) : DlmsPdu;

/// <summary>Encodes and decodes xDLMS and ACSE APDUs (logical name referencing).</summary>
public static class DlmsApdu
{
    // OIDs: 2.16.756.5.8.1.x (application context) and 2.16.756.5.8.2.x (mechanism).
    private static readonly byte[] ContextLn = [0x60, 0x85, 0x74, 0x05, 0x08, 0x01, 0x01];
    private static readonly byte[] ContextLnCiphered = [0x60, 0x85, 0x74, 0x05, 0x08, 0x01, 0x03];
    private static byte[] Mechanism(DlmsAuthentication m) => [0x60, 0x85, 0x74, 0x05, 0x08, 0x02, (byte)m];

    /// <summary>Unciphered InitiateRequest (the xDLMS part of an AARQ).</summary>
    public static byte[] InitiateRequest(DlmsConformance conformance, ushort maxPdu) =>
        [0x01, 0x00, 0x00, 0x00, 0x06, 0x5F, 0x1F, 0x04, 0x00, (byte)((uint)conformance >> 16), (byte)((uint)conformance >> 8), (byte)conformance, (byte)(maxPdu >> 8), (byte)maxPdu];

    /// <summary>Unciphered InitiateResponse.</summary>
    public static byte[] InitiateResponse(DlmsConformance conformance, ushort maxPdu) =>
        [0x08, 0x00, 0x06, 0x5F, 0x1F, 0x04, 0x00, (byte)((uint)conformance >> 16), (byte)((uint)conformance >> 8), (byte)conformance, (byte)(maxPdu >> 8), (byte)maxPdu, 0x00, 0x07];

    /// <summary>Encodes a PDU.</summary>
    public static byte[] Encode(DlmsPdu pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        var b = new List<byte>(32);
        switch (pdu)
        {
            case AarqPdu q:
            {
                var body = new List<byte>();
                Ber(body, 0xA1, Ber(0x06, q.Ciphered ? ContextLnCiphered : ContextLn));
                if (q.SystemTitle is { } st) Ber(body, 0xA6, Ber(0x04, st));
                if (q.Authentication != DlmsAuthentication.None)
                {
                    Ber(body, 0x8A, [0x07, 0x80]);
                    Ber(body, 0x8B, Mechanism(q.Authentication));
                    Ber(body, 0xAC, Ber(0x80, q.AuthenticationValue ?? []));
                }

                Ber(body, 0xBE, Ber(0x04, q.CipheredInitiate ?? InitiateRequest(q.Conformance, q.MaxPduSize)));
                Ber(b, 0x60, [.. body]);
                break;
            }

            case AarePdu r:
            {
                var body = new List<byte>();
                Ber(body, 0xA1, Ber(0x06, r.Ciphered ? ContextLnCiphered : ContextLn));
                Ber(body, 0xA2, [0x02, 0x01, r.Result]);
                Ber(body, 0xA3, Ber(0xA1, [0x02, 0x01, r.Diagnostic]));
                if (r.SystemTitle is { } st) Ber(body, 0xA4, Ber(0x04, st));
                if (r.Authentication != DlmsAuthentication.None)
                {
                    Ber(body, 0x88, [0x07, 0x80]);
                    Ber(body, 0x89, Mechanism(r.Authentication));
                    if (r.AuthenticationValue is { } stoc) Ber(body, 0xAA, Ber(0x80, stoc));
                }

                if (r.Accepted) Ber(body, 0xBE, Ber(0x04, r.CipheredInitiate ?? InitiateResponse(r.Conformance, r.MaxPduSize)));
                Ber(b, 0x61, [.. body]);
                break;
            }

            case ReleasePdu rl:
                b.AddRange(rl.Response ? [0x63, 0x03, 0x80, 0x01, 0x00] : [0x62, 0x03, 0x80, 0x01, 0x00]);
                break;

            case GetRequestPdu g:
                b.AddRange([0xC0, 0x01, g.InvokeId]);
                g.Attribute.Write(b);
                WriteAccess(b, g.Access);
                break;

            case GetNextPdu n:
                b.AddRange([0xC0, 0x02, n.InvokeId]);
                AddUInt32(b, n.BlockNumber);
                break;

            case GetResponsePdu r when !r.IsBlock:
                b.AddRange([0xC4, 0x01, r.InvokeId]);
                if (r.Data is { } data)
                {
                    b.Add(0x00);
                    data.Write(b);
                }
                else
                {
                    b.AddRange([0x01, (byte)r.Result]);
                }

                break;

            case GetResponsePdu r:
                b.AddRange([0xC4, 0x02, r.InvokeId, r.LastBlock ? (byte)1 : (byte)0]);
                AddUInt32(b, r.BlockNumber);
                if (r.Block is { } block)
                {
                    b.Add(0x00);
                    AxdrLength.Write(b, block.Length);
                    b.AddRange(block);
                }
                else
                {
                    b.AddRange([0x01, (byte)r.Result]);
                }

                break;

            case SetRequestPdu s:
                b.AddRange([0xC1, 0x01, s.InvokeId]);
                s.Attribute.Write(b);
                b.Add(0x00);
                s.Value.Write(b);
                break;

            case SetResponsePdu s:
                b.AddRange([0xC5, 0x01, s.InvokeId, (byte)s.Result]);
                break;

            case ActionRequestPdu a:
                b.AddRange([0xC3, 0x01, a.InvokeId]);
                a.Method.Write(b);
                if (a.Parameter is { } p)
                {
                    b.Add(0x01);
                    p.Write(b);
                }
                else
                {
                    b.Add(0x00);
                }

                break;

            case ActionResponsePdu a:
                b.AddRange([0xC7, 0x01, a.InvokeId, (byte)a.Result]);
                if (a.ReturnValue is { } ret)
                {
                    b.AddRange([0x01, 0x00]);
                    ret.Write(b);
                }
                else
                {
                    b.Add(0x00);
                }

                break;

            case CipheredPdu c:
                b.Add(c.Tag);
                AxdrLength.Write(b, 5 + c.Payload.Length);
                b.Add(c.SecurityControl);
                AddUInt32(b, c.InvocationCounter);
                b.AddRange(c.Payload);
                break;

            case ErrorPdu:
                b.AddRange([0xD8, 0x01, 0x01]);
                break;

            default:
                throw new ArgumentException($"Cannot encode {pdu.GetType().Name}.", nameof(pdu));
        }

        return [.. b];
    }

    /// <summary>Decodes a PDU; never throws for malformed input.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out DlmsPdu? pdu, out string? error)
    {
        pdu = null;
        error = null;
        try
        {
            pdu = Decode(data);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            error = "Truncated APDU.";
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "Truncated APDU.";
            return false;
        }
    }

    /// <summary>Decodes a PDU or throws <see cref="FormatException"/>.</summary>
    public static DlmsPdu Decode(ReadOnlySpan<byte> d)
    {
        if (d.IsEmpty) throw new FormatException("Empty APDU.");
        var pos = 0;
        switch (d[0])
        {
            case 0x60:
                return ParseAarq(BerContent(d, ref pos, 0x60));
            case 0x61:
                return ParseAare(BerContent(d, ref pos, 0x61));
            case 0x62:
                return new ReleasePdu(false);
            case 0x63:
                return new ReleasePdu(true);
            case 0xC0 when d.Length >= 3 && d[1] == 0x01:
            {
                pos = 3;
                var attr = CosemReference.Read(d, ref pos);
                return new GetRequestPdu(attr, ReadAccess(d, ref pos)) { InvokeId = d[2] };
            }

            case 0xC0 when d.Length >= 7 && d[1] == 0x02:
                return new GetNextPdu(BinaryPrimitives.ReadUInt32BigEndian(d[3..])) { InvokeId = d[2] };
            case 0xC4 when d.Length >= 4 && d[1] == 0x01:
            {
                pos = 4;
                if (d[3] == 0x00) return new GetResponsePdu { InvokeId = d[2], Data = CosemData.Read(d, ref pos, 0) };
                return new GetResponsePdu { InvokeId = d[2], Result = (DataAccessResult)d[4] };
            }

            case 0xC4 when d.Length >= 9 && d[1] == 0x02:
            {
                var last = d[3] != 0;
                var number = BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
                pos = 9;
                if (d[8] != 0x00) return new GetResponsePdu { InvokeId = d[2], IsBlock = true, LastBlock = last, BlockNumber = number, Result = (DataAccessResult)d[9] };
                var n = AxdrLength.Read(d, ref pos);
                if (pos + n > d.Length) throw new FormatException("Truncated data block.");
                return new GetResponsePdu { InvokeId = d[2], IsBlock = true, LastBlock = last, BlockNumber = number, Block = d.Slice(pos, n).ToArray() };
            }

            case 0xC1 when d.Length >= 3 && d[1] == 0x01:
            {
                pos = 3;
                var attr = CosemReference.Read(d, ref pos);
                ReadAccess(d, ref pos);
                return new SetRequestPdu(attr, CosemData.Read(d, ref pos, 0)) { InvokeId = d[2] };
            }

            case 0xC5 when d.Length >= 4 && d[1] == 0x01:
                return new SetResponsePdu((DataAccessResult)d[3]) { InvokeId = d[2] };
            case 0xC3 when d.Length >= 3 && d[1] == 0x01:
            {
                pos = 3;
                var method = CosemReference.Read(d, ref pos);
                if (pos >= d.Length) throw new FormatException("Truncated ACTION.request.");
                var hasParameter = d[pos++] != 0;
                return new ActionRequestPdu(method, hasParameter ? CosemData.Read(d, ref pos, 0) : null) { InvokeId = d[2] };
            }

            case 0xC7 when d.Length >= 5 && d[1] == 0x01:
            {
                CosemData? ret = null;
                if (d[4] == 0x01 && d.Length > 6 && d[5] == 0x00)
                {
                    pos = 6;
                    ret = CosemData.Read(d, ref pos, 0);
                }

                return new ActionResponsePdu((ActionResult)d[3], ret) { InvokeId = d[2] };
            }

            case 0xD8:
                return new ErrorPdu(d.Length >= 3 ? $"exception-response: state-error {d[1]}, service-error {d[2]}" : "exception-response");
            case 0x0E:
                return new ErrorPdu("confirmed-service-error");
            case 0x21 or 0x28 or (>= 0xC8 and <= 0xCF):
            {
                pos = 1;
                var n = AxdrLength.Read(d, ref pos);
                if (n < 5 || pos + n > d.Length) throw new FormatException("Truncated ciphered APDU.");
                return new CipheredPdu(d[0], d[pos], BinaryPrimitives.ReadUInt32BigEndian(d[(pos + 1)..]), d.Slice(pos + 5, n - 5).ToArray());
            }

            default:
                throw new FormatException($"Unsupported APDU tag 0x{d[0]:X2}.");
        }
    }

    /// <summary>One-line description of a PDU.</summary>
    public static string Describe(DlmsPdu pdu) => pdu switch
    {
        AarqPdu q => $"AARQ {(q.Ciphered ? "ciphered " : "")}auth={q.Authentication}",
        AarePdu r => r.Accepted ? $"AARE accepted, max PDU {r.MaxPduSize}" : $"AARE rejected (diagnostic {r.Diagnostic})",
        ReleasePdu r => r.Response ? "RLRE" : "RLRQ",
        GetRequestPdu g => $"GET {g.Attribute}{(g.Access is { } a ? $" selector {a.Selector}" : "")}",
        GetNextPdu n => $"GET next after block {n.BlockNumber}",
        GetResponsePdu { IsBlock: true, Block: not null } r => $"GET.response block {r.BlockNumber}{(r.LastBlock ? " (last)" : "")} {r.Block!.Length} B",
        GetResponsePdu r => r.Data is { } v ? $"GET.response {Short(v)}" : $"GET.response {r.Result}",
        SetRequestPdu s => $"SET {s.Attribute} = {Short(s.Value)}",
        SetResponsePdu s => $"SET.response {s.Result}",
        ActionRequestPdu a => $"ACTION {a.Method}",
        ActionResponsePdu a => $"ACTION.response {a.Result}",
        CipheredPdu c => $"ciphered 0x{c.Tag:X2} IC {c.InvocationCounter}",
        ErrorPdu e => e.Description,
        _ => pdu.GetType().Name,
    };

    private static string Short(CosemData v)
    {
        var s = v.ToString();
        return s.Length <= 48 ? s : s[..48] + "…";
    }

    private static void WriteAccess(List<byte> b, CosemAccess? access)
    {
        if (access is null)
        {
            b.Add(0x00);
            return;
        }

        b.Add(0x01);
        b.Add(access.Selector);
        access.Parameters.Write(b);
    }

    private static CosemAccess? ReadAccess(ReadOnlySpan<byte> d, ref int pos)
    {
        if (pos >= d.Length) throw new FormatException("Truncated access selection.");
        if (d[pos++] == 0) return null;
        if (pos >= d.Length) throw new FormatException("Truncated access selector.");
        var selector = d[pos++];
        return new CosemAccess(selector, CosemData.Read(d, ref pos, 0));
    }

    private static void AddUInt32(List<byte> b, uint v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    // ---- BER (ACSE) ---------------------------------------------------------------------------------------

    private static byte[] Ber(byte tag, ReadOnlySpan<byte> content)
    {
        var b = new List<byte>(content.Length + 4);
        Ber(b, tag, content);
        return [.. b];
    }

    private static void Ber(List<byte> b, byte tag, ReadOnlySpan<byte> content)
    {
        b.Add(tag);
        AxdrLength.Write(b, content.Length);
        b.AddRange(content);
    }

    private static ReadOnlySpan<byte> BerContent(ReadOnlySpan<byte> d, ref int pos, byte expectedTag)
    {
        if (pos >= d.Length || d[pos] != expectedTag) throw new FormatException($"Expected BER tag 0x{expectedTag:X2}.");
        pos++;
        var n = AxdrLength.Read(d, ref pos);
        if (pos + n > d.Length) throw new FormatException($"Truncated BER element 0x{expectedTag:X2}.");
        var content = d.Slice(pos, n);
        pos += n;
        return content;
    }

    private static Dictionary<byte, byte[]> BerElements(ReadOnlySpan<byte> d)
    {
        var map = new Dictionary<byte, byte[]>();
        var pos = 0;
        while (pos < d.Length)
        {
            var tag = d[pos];
            map[tag] = BerContent(d, ref pos, tag).ToArray();
        }

        return map;
    }

    private static byte[]? Inner(Dictionary<byte, byte[]> map, byte outer, byte inner)
    {
        if (!map.TryGetValue(outer, out var v)) return null;
        var pos = 0;
        return BerContent(v, ref pos, inner).ToArray();
    }

    private static AarqPdu ParseAarq(ReadOnlySpan<byte> content)
    {
        var e = BerElements(content);
        var context = Inner(e, 0xA1, 0x06) ?? throw new FormatException("AARQ without application context.");
        var mechanism = e.TryGetValue(0x8B, out var mech) && mech.Length == 7 ? (DlmsAuthentication)mech[6] : DlmsAuthentication.None;
        var user = Inner(e, 0xBE, 0x04) ?? throw new FormatException("AARQ without user information.");
        var ciphered = context.Length == 7 && context[6] == 3;
        var q = new AarqPdu
        {
            InvokeId = 0,
            Ciphered = ciphered,
            Authentication = mechanism,
            AuthenticationValue = Inner(e, 0xAC, 0x80),
            SystemTitle = Inner(e, 0xA6, 0x04),
        };
        if (user.Length > 0 && user[0] == 0x21) return q with { CipheredInitiate = user };
        var (conformance, maxPdu) = ParseInitiate(user, request: true);
        return q with { Conformance = conformance, MaxPduSize = maxPdu };
    }

    private static AarePdu ParseAare(ReadOnlySpan<byte> content)
    {
        var e = BerElements(content);
        var context = Inner(e, 0xA1, 0x06);
        var result = e.TryGetValue(0xA2, out var r) && r.Length == 3 ? r[2] : (byte)1;
        var diagnosticOuter = e.TryGetValue(0xA3, out var diag) ? diag : [];
        var diagnostic = diagnosticOuter.Length >= 5 ? diagnosticOuter[^1] : (byte)0;
        var mechanism = e.TryGetValue(0x89, out var mech) && mech.Length == 7 ? (DlmsAuthentication)mech[6] : DlmsAuthentication.None;
        var a = new AarePdu
        {
            InvokeId = 0,
            Result = result,
            Diagnostic = diagnostic,
            Ciphered = context is { Length: 7 } && context[6] == 3,
            Authentication = mechanism,
            SystemTitle = Inner(e, 0xA4, 0x04),
            AuthenticationValue = Inner(e, 0xAA, 0x80),
        };
        if (Inner(e, 0xBE, 0x04) is not { Length: > 0 } user) return a;
        if (user[0] == 0x28) return a with { CipheredInitiate = user };
        if (user[0] != 0x08) return a;
        var (conformance, maxPdu) = ParseInitiate(user, request: false);
        return a with { Conformance = conformance, MaxPduSize = maxPdu };
    }

    /// <summary>Parses an (unciphered) InitiateRequest (tag 0x01) or InitiateResponse (tag 0x08).</summary>
    public static (DlmsConformance Conformance, ushort MaxPdu) ParseInitiate(ReadOnlySpan<byte> u, bool request)
    {
        var pos = 1;
        if (request)
        {
            if (pos < u.Length && u[pos++] != 0) pos += 1 + u[pos];   // dedicated key
            if (pos < u.Length && u[pos++] != 0) pos++;               // response-allowed
            if (pos < u.Length && u[pos++] != 0) pos++;               // quality of service
        }
        else if (pos < u.Length && u[pos++] != 0)
        {
            pos++;                                                   // negotiated quality of service
        }

        pos++;                                                       // DLMS version
        if (pos + 4 > u.Length || u[pos] != 0x5F || u[pos + 1] != 0x1F) throw new FormatException("Initiate PDU without a conformance block.");
        pos += 4;                                                    // 5F 1F 04 00
        if (pos + 5 > u.Length) throw new FormatException("Truncated initiate PDU.");
        var conformance = (DlmsConformance)(uint)((u[pos] << 16) | (u[pos + 1] << 8) | u[pos + 2]);
        var maxPdu = (ushort)((u[pos + 3] << 8) | u[pos + 4]);
        return (conformance, maxPdu);
    }
}

/// <summary>Keys and counters of security suite 0 (AES-GCM-128) for one side of an association.</summary>
public sealed class DlmsSecurity
{
    /// <summary>Creates the security context.</summary>
    /// <param name="systemTitle">Own system title (8 bytes).</param>
    /// <param name="blockCipherKey">Global unicast encryption key (EK).</param>
    /// <param name="authenticationKey">Authentication key (AK).</param>
    public DlmsSecurity(byte[] systemTitle, byte[] blockCipherKey, byte[] authenticationKey)
    {
        ArgumentNullException.ThrowIfNull(systemTitle);
        ArgumentNullException.ThrowIfNull(blockCipherKey);
        ArgumentNullException.ThrowIfNull(authenticationKey);
        if (systemTitle.Length != 8) throw new ArgumentException("A system title has 8 bytes.", nameof(systemTitle));
        if (blockCipherKey.Length != 16 || authenticationKey.Length != 16) throw new ArgumentException("Suite 0 keys have 16 bytes.");
        SystemTitle = systemTitle;
        BlockCipherKey = blockCipherKey;
        AuthenticationKey = authenticationKey;
    }

    /// <summary>Own system title.</summary>
    public byte[] SystemTitle { get; }

    /// <summary>EK.</summary>
    public byte[] BlockCipherKey { get; }

    /// <summary>AK.</summary>
    public byte[] AuthenticationKey { get; }

    /// <summary>Next invocation counter to use.</summary>
    public uint InvocationCounter { get; set; }

    /// <summary>The peer's system title (learned from AARQ/AARE).</summary>
    public byte[]? PeerSystemTitle { get; set; }

    /// <summary>Last invocation counter received from the peer (replay protection).</summary>
    public uint? PeerInvocationCounter { get; private set; }

    /// <summary>Authenticates and encrypts <paramref name="apdu"/> (security control 0x30) into a glo-APDU.</summary>
    public byte[] Protect(byte gloTag, ReadOnlySpan<byte> apdu)
    {
        var ic = InvocationCounter++;
        return DlmsApdu.Encode(new CipheredPdu(gloTag, 0x30, ic, Encrypt(SystemTitle, BlockCipherKey, AuthenticationKey, 0x30, ic, apdu)));
    }

    /// <summary>Decrypts and checks a glo-APDU from the peer; throws <see cref="CryptographicException"/> on a bad tag.</summary>
    public byte[] Unprotect(CipheredPdu pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        var title = PeerSystemTitle ?? throw new InvalidOperationException("The peer's system title is unknown.");
        if (PeerInvocationCounter is { } last && pdu.InvocationCounter <= last)
            throw new CryptographicException($"Replayed invocation counter {pdu.InvocationCounter} (last {last}).");
        var plain = Decrypt(title, BlockCipherKey, AuthenticationKey, pdu.SecurityControl, pdu.InvocationCounter, pdu.Payload);
        PeerInvocationCounter = pdu.InvocationCounter;
        return plain;
    }

    /// <summary>
    /// The HLS mechanism 5 response to a challenge: SC 0x10 || IC || GMAC(IV = <paramref name="systemTitle"/> || IC,
    /// AAD = SC || AK || challenge).
    /// </summary>
    public static byte[] Gmac(byte[] systemTitle, byte[] blockCipherKey, byte[] authenticationKey, uint invocationCounter, ReadOnlySpan<byte> challenge)
    {
        ArgumentNullException.ThrowIfNull(systemTitle);
        ArgumentNullException.ThrowIfNull(authenticationKey);
        var aad = new byte[1 + authenticationKey.Length + challenge.Length];
        aad[0] = 0x10;
        authenticationKey.CopyTo(aad, 1);
        challenge.CopyTo(aad.AsSpan(1 + authenticationKey.Length));
        var tag = new byte[12];
        using (var gcm = new AesGcm(blockCipherKey, 12))
            gcm.Encrypt(Nonce(systemTitle, invocationCounter), ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, aad);
        var result = new byte[17];
        result[0] = 0x10;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1), invocationCounter);
        tag.CopyTo(result, 5);
        return result;
    }

    /// <summary>Checks an HLS response from <paramref name="peerSystemTitle"/> to our <paramref name="challenge"/>.</summary>
    public bool VerifyGmac(byte[] peerSystemTitle, ReadOnlySpan<byte> response, ReadOnlySpan<byte> challenge)
    {
        if (response.Length != 17 || response[0] != 0x10) return false;
        var ic = BinaryPrimitives.ReadUInt32BigEndian(response[1..]);
        var expected = Gmac(peerSystemTitle, BlockCipherKey, AuthenticationKey, ic, challenge);
        return CryptographicOperations.FixedTimeEquals(expected, response);
    }

    /// <summary>Suite 0 encryption (SC 0x30: GCM with AAD = SC || AK; 0x10: authentication only; 0x20: encryption only).</summary>
    public static byte[] Encrypt(byte[] systemTitle, byte[] ek, byte[] ak, byte securityControl, uint ic, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(ak);
        var auth = (securityControl & 0x10) != 0;
        var enc = (securityControl & 0x20) != 0;
        var nonce = Nonce(systemTitle, ic);
        using var gcm = new AesGcm(ek, 12);
        var tag = new byte[12];
        if (enc)
        {
            var cipher = new byte[plaintext.Length];
            gcm.Encrypt(nonce, plaintext, cipher, tag, auth ? [securityControl, .. ak] : []);
            return auth ? [.. cipher, .. tag] : cipher;
        }

        if (!auth) return plaintext.ToArray();
        gcm.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, (ReadOnlySpan<byte>)[securityControl, .. ak, .. plaintext]);
        return [.. plaintext, .. tag];
    }

    /// <summary>Suite 0 decryption; throws <see cref="AuthenticationTagMismatchException"/> on a bad tag.</summary>
    public static byte[] Decrypt(byte[] systemTitle, byte[] ek, byte[] ak, byte securityControl, uint ic, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(ak);
        var auth = (securityControl & 0x10) != 0;
        var enc = (securityControl & 0x20) != 0;
        var nonce = Nonce(systemTitle, ic);
        using var gcm = new AesGcm(ek, 12);
        if (auth && payload.Length < 12) throw new CryptographicException("Ciphered APDU shorter than its tag.");
        var body = auth ? payload[..^12] : payload;
        var tag = auth ? payload[^12..] : [];
        if (enc)
        {
            var plain = new byte[body.Length];
            if (auth) gcm.Decrypt(nonce, body, tag, plain, [securityControl, .. ak]);
            else throw new NotSupportedException("Encryption without authentication is not supported (use security control 0x30).");
            return plain;
        }

        if (!auth) return body.ToArray();
        gcm.Decrypt(nonce, ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, (ReadOnlySpan<byte>)[securityControl, .. ak, .. body]);
        return body.ToArray();
    }

    private static byte[] Nonce(byte[] systemTitle, uint ic)
    {
        var nonce = new byte[12];
        systemTitle.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), ic);
        return nonce;
    }

    /// <summary>The glo- tag that protects an unciphered APDU tag (e.g. 0xC0 → 0xC8).</summary>
    public static byte GloTag(byte tag) => tag switch
    {
        0x01 => 0x21,
        0x08 => 0x28,
        >= 0xC0 and <= 0xC7 => (byte)(tag + 8),
        _ => throw new ArgumentException($"APDU 0x{tag:X2} has no global ciphering tag.", nameof(tag)),
    };
}
