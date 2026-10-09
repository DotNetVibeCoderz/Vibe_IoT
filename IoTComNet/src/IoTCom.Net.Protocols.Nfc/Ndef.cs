using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Nfc;

/// <summary>Type name format (the TNF field, 3 bits).</summary>
public enum NdefTnf : byte
{
    /// <summary>Empty record.</summary>
    Empty = 0,
    /// <summary>NFC Forum well-known type ("T", "U", "Sp"…).</summary>
    WellKnown = 1,
    /// <summary>MIME media type (RFC 2046).</summary>
    MimeMedia = 2,
    /// <summary>Absolute URI as the type.</summary>
    AbsoluteUri = 3,
    /// <summary>NFC Forum external type ("example.com:thing").</summary>
    External = 4,
    /// <summary>Unknown payload type.</summary>
    Unknown = 5,
    /// <summary>Continuation chunk of a chunked record.</summary>
    Unchanged = 6,
    /// <summary>Reserved.</summary>
    Reserved = 7,
}

/// <summary>An NDEF record. Factories build the common record types; <c>TryGet…</c> read them back.</summary>
/// <param name="Tnf">Type name format.</param>
/// <param name="Type">Type bytes (ASCII for well-known, MIME and external types).</param>
/// <param name="Id">Optional identifier.</param>
/// <param name="Payload">Payload.</param>
public sealed record NdefRecord(NdefTnf Tnf, byte[] Type, byte[] Id, byte[] Payload)
{
    /// <summary>URI identifier codes (NFC Forum URI RTD, 0x01–0x23).</summary>
    public static readonly IReadOnlyList<string> UriPrefixes =
    [
        "", "http://www.", "https://www.", "http://", "https://", "tel:", "mailto:", "ftp://anonymous:anonymous@", "ftp://ftp.", "ftps://",
        "sftp://", "smb://", "nfs://", "ftp://", "dav://", "news:", "telnet://", "imap:", "rtsp://", "urn:", "pop:", "sip:", "sips:", "tftp:",
        "btspp://", "btl2cap://", "btgoep://", "tcpobex://", "irdaobex://", "file://", "urn:epc:id:", "urn:epc:tag:", "urn:epc:pat:",
        "urn:epc:raw:", "urn:epc:", "urn:nfc:",
    ];

    /// <summary>The type as text.</summary>
    public string TypeText => Encoding.ASCII.GetString(Type);

    private bool Is(NdefTnf tnf, string type) => Tnf == tnf && TypeText == type;

    /// <summary>A Text record (RTD "T", UTF-8).</summary>
    public static NdefRecord Text(string text, string language = "en")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        var lang = Encoding.ASCII.GetBytes(language);
        if (lang.Length is 0 or > 63) throw new ArgumentException("The language code is 1–63 ASCII characters.", nameof(language));
        return new(NdefTnf.WellKnown, "T"u8.ToArray(), [], [(byte)lang.Length, .. lang, .. Encoding.UTF8.GetBytes(text)]);
    }

    /// <summary>A URI record (RTD "U") using the longest matching prefix code.</summary>
    public static NdefRecord Uri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var code = 0;
        for (var i = 1; i < UriPrefixes.Count; i++)
            if (uri.StartsWith(UriPrefixes[i], StringComparison.Ordinal) && UriPrefixes[i].Length > UriPrefixes[code].Length) code = i;
        return new(NdefTnf.WellKnown, "U"u8.ToArray(), [], [(byte)code, .. Encoding.UTF8.GetBytes(uri[UriPrefixes[code].Length..])]);
    }

    /// <summary>A MIME media record.</summary>
    public static NdefRecord Mime(string mimeType, ReadOnlySpan<byte> data) => new(NdefTnf.MimeMedia, Encoding.ASCII.GetBytes(mimeType), [], data.ToArray());

    /// <summary>An external type record ("domain.com:type"; stored lower-case).</summary>
    public static NdefRecord External(string domainType, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(domainType);
        if (!domainType.Contains(':', StringComparison.Ordinal)) throw new ArgumentException("External types look like \"example.com:type\".", nameof(domainType));
        return new(NdefTnf.External, Encoding.ASCII.GetBytes(domainType.ToLowerInvariant()), [], data.ToArray());
    }

    /// <summary>An Android Application Record: opens (or offers to install) the app with this package name.</summary>
    public static NdefRecord AndroidApp(string packageName) => External("android.com:pkg", Encoding.ASCII.GetBytes(packageName));

    /// <summary>A Smart Poster (RTD "Sp"): a URI with an optional title and action.</summary>
    public static NdefRecord SmartPoster(string uri, string? title = null, string language = "en")
    {
        var inner = new List<NdefRecord> { Uri(uri) };
        if (title is not null) inner.Add(Text(title, language));
        return new(NdefTnf.WellKnown, "Sp"u8.ToArray(), [], new NdefMessage(inner).Encode());
    }

    /// <summary>A Wi-Fi credential (Wi-Fi Simple Configuration, "application/vnd.wfa.wsc"). The key is a secret; ToString masks it.</summary>
    public static NdefRecord WifiCredential(WifiCredential credential) => Mime(Nfc.WifiCredential.MimeType, credential.Encode());

    /// <summary>Reads a Text record.</summary>
    public bool TryGetText(out string text, out string language)
    {
        (text, language) = ("", "");
        if (!Is(NdefTnf.WellKnown, "T") || Payload.Length == 0) return false;
        var langLength = Payload[0] & 0x3F;
        if (1 + langLength > Payload.Length) return false;
        language = Encoding.ASCII.GetString(Payload, 1, langLength);
        var body = Payload.AsSpan(1 + langLength);
        text = (Payload[0] & 0x80) != 0 ? DecodeUtf16(body) : Encoding.UTF8.GetString(body);
        return true;
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE) return Encoding.Unicode.GetString(body[2..]);
        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF) body = body[2..];
        return Encoding.BigEndianUnicode.GetString(body);
    }

    /// <summary>Reads a URI record (or an absolute-URI type).</summary>
    public bool TryGetUri(out string uri)
    {
        uri = "";
        if (Tnf == NdefTnf.AbsoluteUri)
        {
            uri = TypeText;
            return true;
        }

        if (!Is(NdefTnf.WellKnown, "U") || Payload.Length == 0) return false;
        uri = (Payload[0] < UriPrefixes.Count ? UriPrefixes[Payload[0]] : "") + Encoding.UTF8.GetString(Payload, 1, Payload.Length - 1);
        return true;
    }

    /// <summary>Reads a Smart Poster's records.</summary>
    public bool TryGetSmartPoster(out NdefMessage content)
    {
        content = new NdefMessage([]);
        if (!Is(NdefTnf.WellKnown, "Sp")) return false;
        try
        {
            content = NdefMessage.Parse(Payload);
            return true;
        }
        catch (ProtocolException)
        {
            return false;
        }
    }

    /// <summary>Reads a Wi-Fi credential.</summary>
    public bool TryGetWifiCredential(out WifiCredential credential)
    {
        credential = default!;
        if (!Is(NdefTnf.MimeMedia, Nfc.WifiCredential.MimeType)) return false;
        return Nfc.WifiCredential.TryParse(Payload, out credential);
    }

    /// <summary>A one-line description (secrets masked).</summary>
    public override string ToString()
    {
        if (TryGetText(out var text, out var lang)) return $"Text ({lang}): {text}";
        if (TryGetUri(out var uri)) return $"URI: {uri}";
        if (TryGetSmartPoster(out var sp)) return $"Smart Poster: {string.Join("; ", sp.Records)}";
        if (TryGetWifiCredential(out var wifi)) return $"Wi-Fi: {wifi}";
        if (Tnf == NdefTnf.External && TypeText == "android.com:pkg") return $"Android app: {Encoding.ASCII.GetString(Payload)}";
        return Tnf switch
        {
            NdefTnf.Empty => "Empty",
            NdefTnf.MimeMedia => $"MIME {TypeText}, {Payload.Length} bytes",
            NdefTnf.External => $"External {TypeText}, {Payload.Length} bytes",
            _ => $"{Tnf} {TypeText}, {Payload.Length} bytes",
        };
    }

    /// <inheritdoc />
    public bool Equals(NdefRecord? other) =>
        other is not null && Tnf == other.Tnf && Type.AsSpan().SequenceEqual(other.Type) && Id.AsSpan().SequenceEqual(other.Id) && Payload.AsSpan().SequenceEqual(other.Payload);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Tnf, Type.Length, Payload.Length);
}

/// <summary>An NDEF message: one or more records.</summary>
/// <param name="Records">The records.</param>
public sealed record NdefMessage(IReadOnlyList<NdefRecord> Records)
{
    /// <summary>Encodes the message (short records when the payload fits in 255 bytes; never chunked).</summary>
    public byte[] Encode()
    {
        if (Records.Count == 0) return [0xD0, 0x00, 0x00];   // an empty message is one empty record
        var b = new List<byte>();
        for (var i = 0; i < Records.Count; i++)
        {
            var r = Records[i];
            if (r.Type.Length > 255 || r.Id.Length > 255) throw new ArgumentException("NDEF type and id are at most 255 bytes.");
            var sr = r.Payload.Length <= 255;
            var header = (byte)((i == 0 ? 0x80 : 0) | (i == Records.Count - 1 ? 0x40 : 0) | (sr ? 0x10 : 0) | (r.Id.Length > 0 ? 0x08 : 0) | ((byte)r.Tnf & 7));
            b.Add(header);
            b.Add((byte)r.Type.Length);
            if (sr) b.Add((byte)r.Payload.Length);
            else
            {
                var n = (uint)r.Payload.Length;
                b.AddRange([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
            }

            if (r.Id.Length > 0) b.Add((byte)r.Id.Length);
            b.AddRange(r.Type);
            b.AddRange(r.Id);
            b.AddRange(r.Payload);
        }

        return [.. b];
    }

    /// <summary>Parses a message, joining chunked records.</summary>
    /// <exception cref="ProtocolException">Malformed message.</exception>
    public static NdefMessage Parse(ReadOnlySpan<byte> d)
    {
        var records = new List<NdefRecord>();
        var p = 0;
        List<byte>? chunk = null;
        (NdefTnf Tnf, byte[] Type, byte[] Id) chunkHead = (NdefTnf.Empty, [], []);
        var first = true;
        while (true)
        {
            if (p >= d.Length) throw new ProtocolException("NDEF message ended before a record with ME set.");
            var h = d[p++];
            bool mb = (h & 0x80) != 0, me = (h & 0x40) != 0, cf = (h & 0x20) != 0, sr = (h & 0x10) != 0, il = (h & 0x08) != 0;
            var tnf = (NdefTnf)(h & 7);
            if (mb != first) throw new ProtocolException(first ? "The first NDEF record must have MB set." : "MB set on a record that is not the first.");
            first = false;
            if (p >= d.Length) throw new ProtocolException("Truncated NDEF record header.");
            int typeLength = d[p++];
            long payloadLength;
            if (sr)
            {
                if (p >= d.Length) throw new ProtocolException("Truncated NDEF record header.");
                payloadLength = d[p++];
            }
            else
            {
                if (p + 4 > d.Length) throw new ProtocolException("Truncated NDEF record header.");
                payloadLength = BinaryPrimitives.ReadUInt32BigEndian(d[p..]);
                p += 4;
            }

            var idLength = 0;
            if (il)
            {
                if (p >= d.Length) throw new ProtocolException("Truncated NDEF record header.");
                idLength = d[p++];
            }

            if (payloadLength > d.Length - p - typeLength - idLength) throw new ProtocolException("NDEF record longer than the message.");
            var type = d.Slice(p, typeLength).ToArray();
            p += typeLength;
            var id = d.Slice(p, idLength).ToArray();
            p += idLength;
            var payload = d.Slice(p, (int)payloadLength);
            p += (int)payloadLength;

            if (tnf == NdefTnf.Empty && (typeLength != 0 || idLength != 0 || payloadLength != 0)) throw new ProtocolException("An empty NDEF record must have no type, id or payload.");
            if (tnf == NdefTnf.Reserved) throw new ProtocolException("NDEF TNF 7 is reserved.");
            if (chunk is null)
            {
                if (tnf == NdefTnf.Unchanged) throw new ProtocolException("TNF 'unchanged' outside a chunked record.");
                if (cf)
                {
                    chunk = [.. payload.ToArray()];
                    chunkHead = (tnf, type, id);
                }
                else
                {
                    records.Add(new NdefRecord(tnf, type, id, payload.ToArray()));
                }
            }
            else
            {
                if (tnf != NdefTnf.Unchanged || typeLength != 0 || il) throw new ProtocolException("A middle or last NDEF chunk must have TNF 'unchanged' and no type or id.");
                chunk.AddRange(payload.ToArray());
                if (!cf)
                {
                    records.Add(new NdefRecord(chunkHead.Tnf, chunkHead.Type, chunkHead.Id, [.. chunk]));
                    chunk = null;
                }
            }

            if (me)
            {
                if (chunk is not null) throw new ProtocolException("ME set inside a chunked record.");
                break;
            }
        }

        return new NdefMessage(records);
    }

    /// <summary>The frame lane: one header, type, id and payload range per record.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> d)
    {
        var f = new List<FrameField>();
        var p = 0;
        var n = 1;
        try
        {
            _ = Parse(d);
        }
        catch (ProtocolException ex)
        {
            return [new FrameField("NDEF", 0, d.Length, FrameFieldKind.Error, ex.Message)];
        }

        while (p < d.Length)
        {
            var start = p;
            var h = d[p];
            bool sr = (h & 0x10) != 0, il = (h & 0x08) != 0;
            int typeLength = d[p + 1];
            var payloadLength = sr ? d[p + 2] : (int)BinaryPrimitives.ReadUInt32BigEndian(d[(p + 2)..]);
            var headerLength = 2 + (sr ? 1 : 4) + (il ? 1 : 0);
            var idLength = il ? d[p + headerLength - 1] : 0;
            var flags = string.Join(" ", new[] { ((h & 0x80) != 0, "MB"), ((h & 0x40) != 0, "ME"), ((h & 0x20) != 0, "CF"), (sr, "SR"), (il, "IL") }.Where(x => x.Item1).Select(x => x.Item2));
            f.Add(new($"Record {n} header", start, headerLength, FrameFieldKind.Header, $"{flags}, TNF {(NdefTnf)(h & 7)}, payload {payloadLength}"));
            p += headerLength;
            if (typeLength > 0) f.Add(new("Type", p, typeLength, FrameFieldKind.Function, Encoding.ASCII.GetString(d.Slice(p, typeLength))));
            p += typeLength;
            if (idLength > 0) f.Add(new("Id", p, idLength, FrameFieldKind.Address, Encoding.ASCII.GetString(d.Slice(p, idLength))));
            p += idLength;
            if (payloadLength > 0)
            {
                var record = new NdefRecord((NdefTnf)(h & 7), d.Slice(start + headerLength, typeLength).ToArray(), [], d.Slice(p, payloadLength).ToArray());
                f.Add(new("Payload", p, payloadLength, FrameFieldKind.Data, record.ToString()));
            }

            p += payloadLength;
            n++;
            if ((h & 0x40) != 0) break;
        }

        return f;
    }

    /// <inheritdoc />
    public bool Equals(NdefMessage? other) => other is not null && Records.SequenceEqual(other.Records);

    /// <inheritdoc />
    public override int GetHashCode() => Records.Count;

    /// <inheritdoc />
    public override string ToString() => string.Join(" | ", Records);
}

/// <summary>Wi-Fi network authentication (WSC Authentication Type).</summary>
public enum WifiAuthentication : ushort
{
    /// <summary>Open network.</summary>
    Open = 0x0001,
    /// <summary>WPA-Personal.</summary>
    WpaPersonal = 0x0002,
    /// <summary>WPA2-Personal.</summary>
    Wpa2Personal = 0x0020,
    /// <summary>WPA/WPA2-Personal mixed mode.</summary>
    WpaWpa2Personal = 0x0022,
}

/// <summary>
/// A Wi-Fi credential as carried by Wi-Fi Simple Configuration NFC tokens (application/vnd.wfa.wsc): SSID,
/// authentication, encryption and network key. <see cref="ToString"/> never shows the key.
/// </summary>
/// <param name="Ssid">Network name.</param>
/// <param name="NetworkKey">Passphrase (empty for open networks).</param>
/// <param name="Authentication">Authentication type.</param>
public sealed record WifiCredential(string Ssid, string NetworkKey, WifiAuthentication Authentication = WifiAuthentication.Wpa2Personal)
{
    /// <summary>MIME type of WSC tokens.</summary>
    public const string MimeType = "application/vnd.wfa.wsc";

    private const ushort Credential = 0x100E, NetworkIndex = 0x1026, SsidTag = 0x1045, AuthType = 0x1003, EncType = 0x100F, Key = 0x1027, Mac = 0x1020;

    /// <summary>Encodes the WSC attributes.</summary>
    public byte[] Encode()
    {
        var inner = new List<byte>();
        void Attr(List<byte> to, ushort type, ReadOnlySpan<byte> value)
        {
            to.Add((byte)(type >> 8));
            to.Add((byte)type);
            to.Add((byte)(value.Length >> 8));
            to.Add((byte)value.Length);
            to.AddRange(value.ToArray());
        }

        Attr(inner, NetworkIndex, [1]);
        Attr(inner, SsidTag, Encoding.UTF8.GetBytes(Ssid));
        Attr(inner, AuthType, [(byte)((ushort)Authentication >> 8), (byte)Authentication]);
        var enc = Authentication == WifiAuthentication.Open ? (ushort)0x0001 : (ushort)0x0008;   // none / AES
        Attr(inner, EncType, [(byte)(enc >> 8), (byte)enc]);
        Attr(inner, Key, Encoding.UTF8.GetBytes(NetworkKey));
        Attr(inner, Mac, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        var outer = new List<byte>();
        Attr(outer, Credential, inner.ToArray());
        return [.. outer];
    }

    /// <summary>Parses the first credential in a WSC payload.</summary>
    public static bool TryParse(ReadOnlySpan<byte> d, out WifiCredential credential)
    {
        credential = default!;
        if (!TryFind(d, Credential, out var cred)) return false;
        if (!TryFind(cred, SsidTag, out var ssid)) return false;
        var key = TryFind(cred, Key, out var k) ? Encoding.UTF8.GetString(k) : "";
        var auth = TryFind(cred, AuthType, out var a) && a.Length == 2 ? (WifiAuthentication)BinaryPrimitives.ReadUInt16BigEndian(a) : WifiAuthentication.Open;
        credential = new WifiCredential(Encoding.UTF8.GetString(ssid), key, auth);
        return true;
    }

    private static bool TryFind(ReadOnlySpan<byte> d, ushort type, out ReadOnlySpan<byte> value)
    {
        value = default;
        var p = 0;
        while (p + 4 <= d.Length)
        {
            var t = BinaryPrimitives.ReadUInt16BigEndian(d[p..]);
            var len = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 2)..]);
            if (p + 4 + len > d.Length) return false;
            if (t == type)
            {
                value = d.Slice(p + 4, len);
                return true;
            }

            p += 4 + len;
        }

        return false;
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"SSID \"{Ssid}\", {Authentication}, key {(NetworkKey.Length == 0 ? "none" : "••••••••")}");
}
