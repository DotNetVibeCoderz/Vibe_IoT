using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>A device attestation chain: DAC and PAI (X.509 DER) and the DAC private key.</summary>
/// <param name="Dac">Device attestation certificate.</param>
/// <param name="Pai">Product attestation intermediate.</param>
/// <param name="DacPrivateKey">DAC private key (32 bytes).</param>
/// <param name="CertificationDeclaration">Certification declaration bytes (CMS-signed by CSA for real products).</param>
public sealed record MatterAttestationChain(byte[] Dac, byte[] Pai, byte[] DacPrivateKey, byte[] CertificationDeclaration);

/// <summary>
/// Device attestation (core spec §6.2): PAA → PAI → DAC chains with the Matter vendor/product id attributes, and their
/// verification. <see cref="TestPaa"/> is IoTCom's own test root — never a production trust anchor.
/// </summary>
public static class MatterAttestation
{
    private const string VidOid = "1.3.6.1.4.1.37244.2.1", PidOid = "1.3.6.1.4.1.37244.2.2";
    // IoTCom's published test PAA (like the CHIP SDK's test PAAs): the key is public on purpose, so a device simulated in one
    // process can be attested by a controller in another. Never trust it for real products.
    private static readonly Lazy<(byte[] Der, byte[] Key)> Paa = new(() => (
        Convert.FromBase64String("MIIBfzCCASWgAwIBAgIIIchd45bZCWEwCgYIKoZIzj0EAwIwITEfMB0GA1UEAxMWSW9UQ29tIE1hdHRlciBUZXN0IFBBQTAgFw0yNjAxMDEwMDAwMDBaGA8yMDU2MDEwMTAwMDAwMFowITEfMB0GA1UEAxMWSW9UQ29tIE1hdHRlciBUZXN0IFBBQTBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABJkLxBPVVCu2eoJPbaUAkSZ1xTKTMur7QUWoe8O17w09bYwJv56CUGeOutmgBA8NzkARw1RFejKf78vEEx0ed+ijRTBDMBIGA1UdEwEB/wQIMAYBAf8CAQEwDgYDVR0PAQH/BAQDAgEGMB0GA1UdDgQWBBQf3eSNvzXchWIuH4KE7mxRXZvDDDAKBggqhkjOPQQDAgNIADBFAiBGHH0P/fgLldiGlHepAmhA3tfKvGB3uKcuO1geUQCxBAIhAKkeNd8e2Y2hVM+xOtsZJx6U8uhJzRXXPA94fP6zgDZ6"),
        Convert.FromHexString("3717F8000B22156A2C5F4427F6645889CDB4372F9659BB339E69C0B6342D5A6C")));

    /// <summary>IoTCom's test PAA certificate (DER), trusted by controllers with <c>TrustTestDevices()</c>.</summary>
    public static byte[] TestPaa => Paa.Value.Der;

    /// <summary>Creates a test DAC chain under <see cref="TestPaa"/>.</summary>
    public static MatterAttestationChain CreateTestChain(ushort vendorId, ushort productId)
    {
        var (paiDer, paiKey) = Issue("IoTCom Matter Test PAI", vendorId, null, Paa.Value.Der, Paa.Value.Key, ca: true);
        var (dacDer, dacKey) = Issue("IoTCom Matter Test DAC", vendorId, productId, paiDer, paiKey, ca: false);
        return new MatterAttestationChain(dacDer, paiDer, dacKey, "IoTCom test certification declaration (not CSA-signed)"u8.ToArray());
    }

    private static (byte[], byte[]) Issue(string cn, ushort vendorId, ushort? productId, byte[] issuerDer, byte[] issuerKey, bool ca)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(cn);
        dn.Add(VidOid, vendorId.ToString("X4", System.Globalization.CultureInfo.InvariantCulture), UniversalTagNumber.UTF8String);
        if (productId is { } p) dn.Add(PidOid, p.ToString("X4", System.Globalization.CultureInfo.InvariantCulture), UniversalTagNumber.UTF8String);
        var req = new CertificateRequest(dn.Build(), key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, ca, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var issuerCert = X509CertificateLoader.LoadCertificate(issuerDer);
        var ip = issuerCert.GetECDsaPublicKey()!.ExportParameters(false);
        using var issuer = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = issuerKey, Q = ip.Q });
        using var cert = req.Create(issuerCert.SubjectName, X509SignatureGenerator.CreateForECDsa(issuer), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10), RandomNumberGenerator.GetBytes(8));
        return (cert.RawData, key.ExportParameters(true).D!);
    }

    /// <summary>True when <paramref name="certDer"/> is signed by the key of <paramref name="issuerDer"/>.</summary>
    public static bool IsSignedBy(byte[] certDer, byte[] issuerDer)
    {
        try
        {
            var outer = new AsnReader(certDer, AsnEncodingRules.DER).ReadSequence();
            var tbs = outer.ReadEncodedValue().ToArray();
            outer.ReadSequence();
            var sig = outer.ReadBitString(out _);
            using var issuer = X509CertificateLoader.LoadCertificate(issuerDer);
            using var key = issuer.GetECDsaPublicKey();
            return key is not null && key.VerifyData(tbs, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The vendor id in a PAI/DAC subject, if present.</summary>
    public static ushort? VendorId(byte[] certDer) => Attribute(certDer, VidOid);

    /// <summary>The product id in a DAC subject, if present.</summary>
    public static ushort? ProductId(byte[] certDer) => Attribute(certDer, PidOid);

    private static ushort? Attribute(byte[] der, string oid)
    {
        using var c = X509CertificateLoader.LoadCertificate(der);
        foreach (var rdn in c.SubjectName.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == oid && ushort.TryParse(rdn.GetSingleElementValue(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
        return null;
    }

    /// <summary>The uncompressed public key of an X.509 certificate.</summary>
    public static byte[] PublicKey(byte[] certDer)
    {
        using var c = X509CertificateLoader.LoadCertificate(certDer);
        var p = (c.GetECDsaPublicKey() ?? throw new ProtocolException("Certificate without an EC key.")).ExportParameters(false);
        return [0x04, .. p.Q.X!, .. p.Q.Y!];
    }

    /// <summary>
    /// Verifies DAC → PAI → one of <paramref name="trustedPaas"/>, consistent vendor ids, and the attestation signature over
    /// <paramref name="signedElements"/> ‖ <paramref name="challenge"/>. Returns null when valid, else the reason.
    /// </summary>
    public static string? Verify(byte[] dac, byte[] pai, IEnumerable<byte[]> trustedPaas, byte[] signedElements, byte[] challenge, byte[] signature, ushort expectedVendorId)
    {
        if (!IsSignedBy(dac, pai)) return "the DAC is not signed by the PAI";
        if (!trustedPaas.Any(paa => IsSignedBy(pai, paa))) return "the PAI does not chain to a trusted PAA";
        var dacVid = VendorId(dac);
        var paiVid = VendorId(pai);
        if (dacVid is null || (paiVid is not null && paiVid != dacVid)) return "the DAC and PAI vendor ids differ";
        if (dacVid != expectedVendorId) return $"the DAC is for vendor 0x{dacVid:X4}, the device reports 0x{expectedVendorId:X4}";
        return MatterCrypto.Verify(PublicKey(dac), [.. signedElements, .. challenge], signature) ? null : "the attestation signature does not verify";
    }
}

/// <summary>A fabric as a node stores it.</summary>
internal sealed class MatterFabric
{
    public required byte Index { get; init; }
    public required ulong FabricId { get; init; }
    public required ulong NodeId { get; init; }
    public required MatterCertificate Root { get; init; }
    public required MatterCertificate Noc { get; init; }
    public required byte[] OperationalKey { get; init; }
    public required byte[] EpochKey { get; init; }
    public ulong CaseAdminSubject { get; init; }
    public ushort VendorId { get; init; }
    public string Label { get; set; } = "";

    public byte[] Ipk => MatterCertificate.OperationalIpk(EpochKey, MatterCertificate.CompressedFabricId(Root.PublicKey, FabricId));

    public byte[] DestinationId(byte[] initiatorRandom, ulong nodeId)
    {
        var tail = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(tail, FabricId);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(8), nodeId);
        return MatterCrypto.Hmac(Ipk, initiatorRandom, Root.PublicKey, tail);
    }
}

/// <summary>CASE (Sigma) message building and key schedule shared by initiator and responder.</summary>
internal static class Case
{
    public static readonly byte[] Sigma2Nonce = "NCASE_Sigma2N"u8.ToArray();
    public static readonly byte[] Sigma3Nonce = "NCASE_Sigma3N"u8.ToArray();

    public static byte[] Tbs(MatterCertificate noc, byte[] ownEphemeral, byte[] peerEphemeral) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Bytes(TlvTag.Context(1), noc.EncodeTlv()), TlvElement.Bytes(TlvTag.Context(3), ownEphemeral), TlvElement.Bytes(TlvTag.Context(4), peerEphemeral)));

    public static byte[] S2K(byte[] shared, byte[] ipk, byte[] responderRandom, byte[] responderEphemeral, byte[] sigma1)
        => MatterCrypto.Hkdf(shared, [.. ipk, .. responderRandom, .. responderEphemeral, .. MatterCrypto.Sha256(sigma1)], "Sigma2", 16);

    public static byte[] S3K(byte[] shared, byte[] ipk, byte[] sigma1, byte[] sigma2)
        => MatterCrypto.Hkdf(shared, [.. ipk, .. MatterCrypto.Sha256(sigma1, sigma2)], "Sigma3", 16);

    public static (byte[] I2R, byte[] R2I, byte[] Challenge) SessionKeys(byte[] shared, byte[] ipk, byte[] sigma1, byte[] sigma2, byte[] sigma3)
    {
        var k = MatterCrypto.Hkdf(shared, [.. ipk, .. MatterCrypto.Sha256(sigma1, sigma2, sigma3)], "SessionKeys", 48);
        return (k[..16], k[16..32], k[32..]);
    }

    /// <summary>Checks a peer NOC: signed by the fabric root, same fabric id, and (when given) the expected node id.</summary>
    public static MatterCertificate VerifyPeer(byte[] nocTlv, MatterFabric fabric, ulong? expectedNodeId)
    {
        var noc = MatterCertificate.Parse(nocTlv);
        if (!noc.VerifySignature(fabric.Root.PublicKey)) throw new ProtocolException("The peer NOC is not signed by the fabric root.");
        if (noc.FabricId != fabric.FabricId) throw new ProtocolException($"The peer NOC is for fabric 0x{noc.FabricId:X}, not 0x{fabric.FabricId:X}.");
        if (expectedNodeId is { } n && noc.NodeId != n) throw new ProtocolException($"The peer NOC is for node 0x{noc.NodeId:X}, not 0x{n:X}.");
        return noc;
    }
}

/// <summary>PASE message formats.</summary>
internal static class Pase
{
    public static byte[] ParamRequest(byte[] initiatorRandom, ushort sessionId) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Bytes(TlvTag.Context(1), initiatorRandom), TlvElement.UInt(TlvTag.Context(2), sessionId), TlvElement.UInt(TlvTag.Context(3), 0), TlvElement.Bool(TlvTag.Context(4), false)));

    public static byte[] ParamResponse(byte[] initiatorRandom, byte[] responderRandom, ushort sessionId, int iterations, byte[] salt) => Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
        TlvElement.Bytes(TlvTag.Context(1), initiatorRandom), TlvElement.Bytes(TlvTag.Context(2), responderRandom), TlvElement.UInt(TlvTag.Context(3), sessionId),
        TlvElement.Structure(TlvTag.Context(4), TlvElement.UInt(TlvTag.Context(1), (ulong)iterations), TlvElement.Bytes(TlvTag.Context(2), salt))));
}
