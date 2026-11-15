using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace IoTCom.Net.Transport.Dtls;

/// <summary>DTLS 1.2 cipher suites offered by this package (RFC 7925 profiles for constrained devices first).</summary>
public enum DtlsCipherSuite
{
    /// <summary>TLS_PSK_WITH_AES_128_CCM_8 (CoAP/LwM2M mandatory PSK suite).</summary>
    PskAes128Ccm8 = 0xC0A8,
    /// <summary>TLS_PSK_WITH_AES_128_GCM_SHA256.</summary>
    PskAes128GcmSha256 = 0x00A8,
    /// <summary>TLS_ECDHE_ECDSA_WITH_AES_128_CCM_8 (CoAP certificate mode).</summary>
    EcdheEcdsaAes128Ccm8 = 0xC0AE,
    /// <summary>TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256.</summary>
    EcdheEcdsaAes128GcmSha256 = 0xC02B,
}

/// <summary>A pre-shared key: identity and secret. The secret is never logged.</summary>
public sealed class DtlsPsk
{
    /// <summary>Creates a key.</summary>
    public DtlsPsk(string identity, byte[] key)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        if (key is null || key.Length == 0) throw new ArgumentException("A PSK needs at least one byte.", nameof(key));
        (Identity, Key) = (identity, key);
    }

    /// <summary>Identity (sent in clear text during the handshake).</summary>
    public string Identity { get; }

    /// <summary>Secret.</summary>
    internal byte[] Key { get; }

    /// <summary>A key from a hex string.</summary>
    public static DtlsPsk FromHex(string identity, string hex) => new(identity, Convert.FromHexString(hex));

    /// <inheritdoc />
    public override string ToString() => $"PSK identity '{Identity}' ({Key.Length} bytes)";

    internal byte[] IdentityBytes => Encoding.UTF8.GetBytes(Identity);
}

/// <summary>Options for a DTLS client.</summary>
public sealed class DtlsClientOptions
{
    /// <summary>Pre-shared key (PSK mode).</summary>
    public DtlsPsk? Psk { get; set; }

    /// <summary>
    /// Accepted server certificates in certificate mode: SHA-256 fingerprints (hex) of the leaf certificate. When empty and
    /// <see cref="TrustedRoots"/> is empty, certificate mode refuses every server.
    /// </summary>
    public ISet<string> PinnedFingerprints { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Certificates the server's chain may end in (certificate mode).</summary>
    public X509Certificate2Collection TrustedRoots { get; } = [];

    /// <summary>Cipher suites to offer (default: PSK suites when <see cref="Psk"/> is set, else ECDHE_ECDSA suites).</summary>
    public IList<DtlsCipherSuite> CipherSuites { get; } = [];

    /// <summary>Handshake timeout (default 10 s; retransmissions follow RFC 6347's doubling timer).</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Options for a DTLS server.</summary>
public sealed class DtlsServerOptions
{
    /// <summary>Returns the key for a PSK identity, or null to refuse it (PSK mode).</summary>
    public Func<string, byte[]?>? PskLookup { get; set; }

    /// <summary>Server certificate with an ECDSA P-256 private key (certificate mode).</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>Cipher suites to accept (default: all that the configured credentials allow).</summary>
    public IList<DtlsCipherSuite> CipherSuites { get; } = [];

    /// <summary>Answer a first ClientHello with a HelloVerifyRequest cookie before doing any work (default true; prevents amplification).</summary>
    public bool VerifyRequests { get; set; } = true;

    /// <summary>Handshake timeout per peer (default 10 s).</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Peers without traffic for this long are forgotten (default 5 min).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Uses a dictionary of identities and keys.</summary>
    public DtlsServerOptions UsePsk(IReadOnlyDictionary<string, byte[]> keys)
    {
        PskLookup = id => keys.TryGetValue(id, out var k) ? k : null;
        return this;
    }

    /// <summary>Uses one key.</summary>
    public DtlsServerOptions UsePsk(DtlsPsk psk)
    {
        PskLookup = id => id == psk.Identity ? psk.Key : null;
        return this;
    }
}

/// <summary>Helpers for certificates.</summary>
public static class DtlsCertificates
{
    /// <summary>Creates a self-signed ECDSA P-256 certificate (tests, labs, private deployments that pin fingerprints).</summary>
    public static X509Certificate2 CreateSelfSigned(string subject, TimeSpan? validity = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var cert = request.CreateSelfSigned(now, now + (validity ?? TimeSpan.FromDays(365)));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12, ""), "", X509KeyStorageFlags.Exportable);
    }

    /// <summary>SHA-256 fingerprint (hex) of a certificate, as used by <see cref="DtlsClientOptions.PinnedFingerprints"/>.</summary>
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));
}
