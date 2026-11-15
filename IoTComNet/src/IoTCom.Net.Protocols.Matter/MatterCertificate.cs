using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>Distinguished-name attributes of Matter certificates (context tags in the TLV form).</summary>
public enum MatterDnTag : byte
{
    /// <summary>Common name.</summary>
    CommonName = 1,
    /// <summary>Matter node id.</summary>
    NodeId = 17,
    /// <summary>Matter firmware signing id.</summary>
    FirmwareSigningId = 18,
    /// <summary>Intermediate CA id.</summary>
    IcacId = 19,
    /// <summary>Root CA id.</summary>
    RcacId = 20,
    /// <summary>Fabric id.</summary>
    FabricId = 21,
    /// <summary>CASE authenticated tag.</summary>
    NocCat = 22,
}

/// <summary>
/// A Matter operational certificate (core spec §6.5) in its TLV form, with the exact X.509 DER TBSCertificate the
/// signature covers, issuing (RCAC, ICAC, NOC) and verification.
/// </summary>
public sealed class MatterCertificate
{
    /// <summary>Matter epoch: 2000-01-01T00:00:00Z.</summary>
    public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Serial number.</summary>
    public required byte[] Serial { get; init; }

    /// <summary>Issuer DN (attribute, value: ulong for Matter ids, string for common names).</summary>
    public required IReadOnlyList<(MatterDnTag Tag, object Value)> Issuer { get; init; }

    /// <summary>Subject DN.</summary>
    public required IReadOnlyList<(MatterDnTag Tag, object Value)> Subject { get; init; }

    /// <summary>Not before (seconds since the Matter epoch).</summary>
    public uint NotBefore { get; init; }

    /// <summary>Not after (0 = no expiry).</summary>
    public uint NotAfter { get; init; }

    /// <summary>Uncompressed P-256 public key.</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>Basic constraints: CA.</summary>
    public bool IsCa { get; init; }

    /// <summary>Key usage bitmap (0x01 digitalSignature, 0x20 keyCertSign, 0x40 cRLSign…).</summary>
    public ushort KeyUsage { get; init; }

    /// <summary>Extended key usages (1 serverAuth, 2 clientAuth).</summary>
    public IReadOnlyList<byte> ExtendedKeyUsage { get; init; } = [];

    /// <summary>Subject key id (20 bytes).</summary>
    public required byte[] SubjectKeyId { get; init; }

    /// <summary>Authority key id (20 bytes).</summary>
    public required byte[] AuthorityKeyId { get; init; }

    /// <summary>Signature r‖s.</summary>
    public byte[] Signature { get; init; } = [];

    /// <summary>A subject attribute as an id.</summary>
    public ulong? SubjectId(MatterDnTag tag) => Subject.FirstOrDefault(a => a.Tag == tag).Value is ulong v ? v : null;

    /// <summary>Node id (NOC).</summary>
    public ulong? NodeId => SubjectId(MatterDnTag.NodeId);

    /// <summary>Fabric id.</summary>
    public ulong? FabricId => SubjectId(MatterDnTag.FabricId);

    // ---- TLV

    private static TlvElement Dn(byte tag, IReadOnlyList<(MatterDnTag Tag, object Value)> dn) => TlvElement.List(TlvTag.Context(tag),
        [.. dn.Select(a => a.Value is string s ? TlvElement.String(TlvTag.Context((byte)a.Tag), s) : TlvElement.UInt(TlvTag.Context((byte)a.Tag), Convert.ToUInt64(a.Value, CultureInfo.InvariantCulture)))]);

    /// <summary>The TLV encoding.</summary>
    public byte[] EncodeTlv()
    {
        var ext = new List<TlvElement> { TlvElement.Structure(TlvTag.Context(1), IsCa ? [TlvElement.Bool(TlvTag.Context(1), true)] : [TlvElement.Bool(TlvTag.Context(1), false)]) };
        ext.Add(TlvElement.UInt(TlvTag.Context(2), KeyUsage));
        if (ExtendedKeyUsage.Count > 0) ext.Add(TlvElement.Array(TlvTag.Context(3), [.. ExtendedKeyUsage.Select(e => TlvElement.UInt(TlvTag.Anonymous, e))]));
        ext.Add(TlvElement.Bytes(TlvTag.Context(4), SubjectKeyId));
        ext.Add(TlvElement.Bytes(TlvTag.Context(5), AuthorityKeyId));
        return Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
            TlvElement.Bytes(TlvTag.Context(1), Serial),
            TlvElement.UInt(TlvTag.Context(2), 1),
            Dn(3, Issuer),
            TlvElement.UInt(TlvTag.Context(4), NotBefore),
            TlvElement.UInt(TlvTag.Context(5), NotAfter),
            Dn(6, Subject),
            TlvElement.UInt(TlvTag.Context(7), 1),
            TlvElement.UInt(TlvTag.Context(8), 1),
            TlvElement.Bytes(TlvTag.Context(9), PublicKey),
            TlvElement.List(TlvTag.Context(10), [.. ext]),
            TlvElement.Bytes(TlvTag.Context(11), Signature)));
    }

    /// <summary>Parses the TLV form.</summary>
    public static MatterCertificate Parse(ReadOnlySpan<byte> tlv)
    {
        var e = Tlv.Decode(tlv);
        if (e.Type != TlvType.Structure) throw new ProtocolException("A Matter certificate is a TLV structure.");
        if (e.Required(2).AsUInt() != 1 || e.Required(7).AsUInt() != 1 || e.Required(8).AsUInt() != 1) throw new ProtocolException("Only ECDSA-SHA256 on P-256 certificates are supported.");
        static List<(MatterDnTag, object)> ReadDn(TlvElement list) => [.. list.Children.Select(a => ((MatterDnTag)(a.Tag.Number & 0x7F), a.Type == TlvType.Utf8 ? (object)a.AsString() : a.AsUInt()))];
        var ext = e.Required(10);
        return new MatterCertificate
        {
            Serial = e.Required(1).AsBytes(),
            Issuer = ReadDn(e.Required(3)),
            NotBefore = (uint)e.Required(4).AsUInt(),
            NotAfter = (uint)e.Required(5).AsUInt(),
            Subject = ReadDn(e.Required(6)),
            PublicKey = e.Required(9).AsBytes(),
            IsCa = ext[1]?[1]?.AsBool() ?? false,
            KeyUsage = (ushort)(ext[2]?.AsUInt() ?? 0),
            ExtendedKeyUsage = ext[3]?.Children.Select(c => (byte)c.AsUInt()).ToList() ?? [],
            SubjectKeyId = ext[4]?.AsBytes() ?? [],
            AuthorityKeyId = ext[5]?.AsBytes() ?? [],
            Signature = e.Required(11).AsBytes(),
        };
    }

    // ---- DER

    private static string OidOf(MatterDnTag tag) => tag switch
    {
        MatterDnTag.CommonName => "2.5.4.3",
        MatterDnTag.NodeId => "1.3.6.1.4.1.37244.1.1",
        MatterDnTag.FirmwareSigningId => "1.3.6.1.4.1.37244.1.2",
        MatterDnTag.IcacId => "1.3.6.1.4.1.37244.1.3",
        MatterDnTag.RcacId => "1.3.6.1.4.1.37244.1.4",
        MatterDnTag.FabricId => "1.3.6.1.4.1.37244.1.5",
        MatterDnTag.NocCat => "1.3.6.1.4.1.37244.1.6",
        _ => throw new NotSupportedException($"DN attribute {tag} is not supported."),
    };

    private static void WriteName(AsnWriter w, IReadOnlyList<(MatterDnTag Tag, object Value)> dn)
    {
        using (w.PushSequence())
        {
            foreach (var (tag, value) in dn)
            {
                using (w.PushSetOf())
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier(OidOf(tag));
                    var text = value is string s ? s : tag == MatterDnTag.NocCat
                        ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString("X8", CultureInfo.InvariantCulture)
                        : Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString("X16", CultureInfo.InvariantCulture);
                    w.WriteCharacterString(UniversalTagNumber.UTF8String, text);
                }
            }
        }
    }

    private static void WriteTime(AsnWriter w, uint matterTime, bool notAfter)
    {
        if (notAfter && matterTime == 0)
        {
            w.WriteGeneralizedTime(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero), omitFractionalSeconds: true);
            return;
        }

        var t = Epoch.AddSeconds(matterTime);
        if (t.Year < 2050) w.WriteUtcTime(t);
        else w.WriteGeneralizedTime(t, omitFractionalSeconds: true);
    }

    /// <summary>The X.509 TBSCertificate DER the signature covers.</summary>
    public byte[] TbsDer()
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                w.WriteInteger(2);
            w.WriteIntegerUnsigned(Minimal(Serial));
            using (w.PushSequence())
                w.WriteObjectIdentifier("1.2.840.10045.4.3.2");
            WriteName(w, Issuer);
            using (w.PushSequence())
            {
                WriteTime(w, NotBefore, notAfter: false);
                WriteTime(w, NotAfter, notAfter: true);
            }

            WriteName(w, Subject);
            using (w.PushSequence())
            {
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier("1.2.840.10045.2.1");
                    w.WriteObjectIdentifier("1.2.840.10045.3.1.7");
                }

                w.WriteBitString(PublicKey);
            }

            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3)))
            using (w.PushSequence())
            {
                Extension(w, "2.5.29.19", critical: true, x =>
                {
                    using (x.PushSequence())
                        if (IsCa) x.WriteBoolean(true);
                });
                Extension(w, "2.5.29.15", critical: true, x => x.WriteNamedBitList(KeyUsageFlags()));
                if (ExtendedKeyUsage.Count > 0)
                {
                    Extension(w, "2.5.29.37", critical: true, x =>
                    {
                        using (x.PushSequence())
                            foreach (var e in ExtendedKeyUsage) x.WriteObjectIdentifier(e switch { 1 => "1.3.6.1.5.5.7.3.1", 2 => "1.3.6.1.5.5.7.3.2", 3 => "1.3.6.1.5.5.7.3.3", 4 => "1.3.6.1.5.5.7.3.4", 5 => "1.3.6.1.5.5.7.3.8", _ => "1.3.6.1.5.5.7.3.9" });
                    });
                }

                Extension(w, "2.5.29.14", critical: false, x => x.WriteOctetString(SubjectKeyId));
                Extension(w, "2.5.29.35", critical: false, x =>
                {
                    using (x.PushSequence())
                        x.WriteOctetString(AuthorityKeyId, new Asn1Tag(TagClass.ContextSpecific, 0));
                });
            }
        }

        return w.Encode();
    }

    private X509KeyUsageFlags KeyUsageFlags()
    {
        var f = X509KeyUsageFlags.None;
        if ((KeyUsage & 0x01) != 0) f |= X509KeyUsageFlags.DigitalSignature;
        if ((KeyUsage & 0x02) != 0) f |= X509KeyUsageFlags.NonRepudiation;
        if ((KeyUsage & 0x04) != 0) f |= X509KeyUsageFlags.KeyEncipherment;
        if ((KeyUsage & 0x08) != 0) f |= X509KeyUsageFlags.DataEncipherment;
        if ((KeyUsage & 0x10) != 0) f |= X509KeyUsageFlags.KeyAgreement;
        if ((KeyUsage & 0x20) != 0) f |= X509KeyUsageFlags.KeyCertSign;
        if ((KeyUsage & 0x40) != 0) f |= X509KeyUsageFlags.CrlSign;
        if ((KeyUsage & 0x80) != 0) f |= X509KeyUsageFlags.EncipherOnly;
        if ((KeyUsage & 0x100) != 0) f |= X509KeyUsageFlags.DecipherOnly;
        return f;
    }

    private static void Extension(AsnWriter w, string oid, bool critical, Action<AsnWriter> value)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            if (critical) w.WriteBoolean(true);
            var inner = new AsnWriter(AsnEncodingRules.DER);
            value(inner);
            w.WriteOctetString(inner.Encode());
        }
    }

    /// <summary>The certificate as X.509 DER (TBS, algorithm, DER signature) — what other tools load.</summary>
    public byte[] ToX509Der()
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteEncodedValue(TbsDer());
            using (w.PushSequence())
                w.WriteObjectIdentifier("1.2.840.10045.4.3.2");
            var sig = new AsnWriter(AsnEncodingRules.DER);
            using (sig.PushSequence())
            {
                sig.WriteIntegerUnsigned(Minimal(Signature.AsSpan(0, 32)));
                sig.WriteIntegerUnsigned(Minimal(Signature.AsSpan(32, 32)));
            }

            w.WriteBitString(sig.Encode());
        }

        return w.Encode();
    }

    /// <summary>An unsigned big-endian integer without redundant leading zero bytes (DER requires the minimal form).</summary>
    internal static ReadOnlySpan<byte> Minimal(ReadOnlySpan<byte> value)
    {
        var i = 0;
        while (i < value.Length - 1 && value[i] == 0) i++;
        return value[i..];
    }

    /// <summary>Verifies the signature with the issuer's public key.</summary>
    public bool VerifySignature(byte[] issuerPublicKey) => MatterCrypto.Verify(issuerPublicKey, TbsDer(), Signature);

    // ---- issuing

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Key identifiers are SHA-1 of the public key by definition (RFC 5280 §4.2.1.2 method 1, required by the Matter spec); not a security use.")]
    private static byte[] KeyId(byte[] publicKey) => SHA1.HashData(publicKey);

    /// <summary>Signs a certificate body with <paramref name="issuerPrivateKey"/>.</summary>
    public MatterCertificate SignWith(byte[] issuerPrivateKey)
    {
        var unsigned = this;
        return new MatterCertificate
        {
            Serial = Serial, Issuer = Issuer, Subject = Subject, NotBefore = NotBefore, NotAfter = NotAfter, PublicKey = PublicKey, IsCa = IsCa,
            KeyUsage = KeyUsage, ExtendedKeyUsage = ExtendedKeyUsage, SubjectKeyId = SubjectKeyId, AuthorityKeyId = AuthorityKeyId,
            Signature = MatterCrypto.Sign(issuerPrivateKey, unsigned.TbsDer()),
        };
    }

    private static uint Now() => (uint)(DateTimeOffset.UtcNow - Epoch).TotalSeconds - 3600;

    /// <summary>A self-signed root CA (RCAC).</summary>
    public static MatterCertificate CreateRoot(byte[] privateKey, ulong rcacId, ulong? fabricId = null, uint validSeconds = 10 * 365 * 86400)
    {
        var pub = MatterCrypto.PublicKeyOf(privateKey);
        List<(MatterDnTag, object)> dn = [(MatterDnTag.RcacId, rcacId)];
        if (fabricId is { } f) dn.Add((MatterDnTag.FabricId, f));
        var nb = Now();
        return new MatterCertificate
        {
            Serial = MatterCrypto.RandomBytes(8), Issuer = dn, Subject = dn, NotBefore = nb, NotAfter = nb + validSeconds, PublicKey = pub, IsCa = true,
            KeyUsage = 0x60, SubjectKeyId = KeyId(pub), AuthorityKeyId = KeyId(pub),
        }.SignWith(privateKey);
    }

    /// <summary>A node operational certificate (NOC) for <paramref name="subjectPublicKey"/>, issued by a root.</summary>
    public static MatterCertificate CreateNoc(byte[] subjectPublicKey, ulong nodeId, ulong fabricId, MatterCertificate issuer, byte[] issuerPrivateKey, uint validSeconds = 10 * 365 * 86400)
    {
        var nb = Now();
        return new MatterCertificate
        {
            Serial = MatterCrypto.RandomBytes(8), Issuer = issuer.Subject, Subject = [(MatterDnTag.NodeId, nodeId), (MatterDnTag.FabricId, fabricId)],
            NotBefore = nb, NotAfter = nb + validSeconds, PublicKey = subjectPublicKey, IsCa = false, KeyUsage = 0x01, ExtendedKeyUsage = [2, 1],
            SubjectKeyId = KeyId(subjectPublicKey), AuthorityKeyId = issuer.SubjectKeyId,
        }.SignWith(issuerPrivateKey);
    }

    /// <summary>The public key from a PKCS#10 CSR (the signature is verified).</summary>
    public static byte[] PublicKeyFromCsr(byte[] csr)
    {
        CertificateRequest request;
        try
        {
            request = CertificateRequest.LoadSigningRequest(csr, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException ex)
        {
            throw new ProtocolException($"Invalid CSR: {ex.Message}");
        }

        using var ec = request.PublicKey.GetECDsaPublicKey() ?? throw new ProtocolException("The CSR does not carry an EC public key.");
        var p = ec.ExportParameters(false);
        if (p.Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new ProtocolException("The CSR key is not P-256.");
        return [0x04, .. p.Q.X!, .. p.Q.Y!];
    }

    /// <summary>A PKCS#10 CSR for a P-256 key (subject O=CSA, as the SDK does).</summary>
    public static byte[] CreateCsr(byte[] privateKey)
    {
        var pub = MatterCrypto.PublicKeyOf(privateKey);
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateKey, Q = new ECPoint { X = pub[1..33], Y = pub[33..] } });
        return new CertificateRequest("O=CSA", ec, HashAlgorithmName.SHA256).CreateSigningRequest();
    }

    /// <summary>The compressed fabric id: HKDF(root public key without the 0x04 prefix, salt = fabric id big-endian, "CompressedFabric", 8).</summary>
    public static byte[] CompressedFabricId(byte[] rootPublicKey, ulong fabricId)
    {
        var salt = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(salt, fabricId);
        return MatterCrypto.Hkdf(rootPublicKey[1..], salt, "CompressedFabric", 8);
    }

    /// <summary>The operational group key (IPK) from the epoch key: HKDF(epoch key, salt = compressed fabric id, "GroupKey v1.0", 16).</summary>
    public static byte[] OperationalIpk(byte[] epochKey, byte[] compressedFabricId) => MatterCrypto.Hkdf(epochKey, compressedFabricId, "GroupKey v1.0", 16);

    /// <inheritdoc />
    public override string ToString() => $"{(IsCa ? "CA" : "NOC")} subject {string.Join(',', Subject.Select(a => $"{a.Tag}={(a.Value is ulong v ? v.ToString("X", CultureInfo.InvariantCulture) : a.Value)}"))}";
}
