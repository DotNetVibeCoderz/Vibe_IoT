using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace IoTCom.Net.Transport.Dtls;

/// <summary>A BouncyCastle <see cref="DatagramTransport"/> for one peer: datagrams are pushed in, sends go out through a delegate.</summary>
internal sealed class PeerDatagramTransport(Action<byte[]> send, int mtu = 1400) : DatagramTransport
{
    private readonly Channel<byte[]> _inbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile bool _closed;

    public void Deliver(byte[] datagram) => _inbox.Writer.TryWrite(datagram);

    public int GetReceiveLimit() => 65535;

    public int GetSendLimit() => mtu;

    public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);

    public int Receive(Span<byte> buffer, int waitMillis)
    {
        if (_closed) throw new TlsFatalAlert(AlertDescription.internal_error);
        if (!_inbox.Reader.TryRead(out var d))
        {
            using var cts = new CancellationTokenSource(Math.Max(1, waitMillis));
            try
            {
                if (!_inbox.Reader.WaitToReadAsync(cts.Token).AsTask().GetAwaiter().GetResult()) throw new TlsFatalAlert(AlertDescription.internal_error);
            }
            catch (OperationCanceledException)
            {
                return -1;
            }

            if (!_inbox.Reader.TryRead(out d)) return -1;
        }

        var n = Math.Min(d.Length, buffer.Length);
        d.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));

    public void Send(ReadOnlySpan<byte> buffer)
    {
        if (_closed) throw new TlsFatalAlert(AlertDescription.internal_error);
        send(buffer.ToArray());
    }

    public void Close()
    {
        _closed = true;
        _inbox.Writer.TryComplete();
    }
}

/// <summary>Maps the public options onto BouncyCastle's TLS classes.</summary>
internal static class DtlsBc
{
    public static BcTlsCrypto NewCrypto() => new(new SecureRandom());

    public static int[] Suites(IEnumerable<DtlsCipherSuite> suites) => [.. suites.Select(s => (int)s)];

    public static string SuiteName(int suite) => Enum.IsDefined(typeof(DtlsCipherSuite), suite) ? ((DtlsCipherSuite)suite).ToString() : $"0x{suite:X4}";

    public static void Validate(X509Certificate2 leaf, DtlsClientOptions options)
    {
        if (options.PinnedFingerprints.Contains(DtlsCertificates.Fingerprint(leaf))) return;
        if (options.TrustedRoots.Count > 0)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(options.TrustedRoots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (chain.Build(leaf)) return;
        }

        throw new TlsFatalAlert(AlertDescription.bad_certificate);
    }
}

/// <summary>A DTLS 1.2 client for PSK or ECDHE_ECDSA with certificate pinning or custom roots.</summary>
internal sealed class IoTComPskClient(TlsCrypto crypto, DtlsClientOptions options)
    : PskTlsClient(crypto, new BasicTlsPskIdentity(options.Psk!.IdentityBytes, options.Psk.Key))
{
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    protected override int[] GetSupportedCipherSuites() => DtlsBc.Suites(options.CipherSuites.Count > 0 ? options.CipherSuites : [DtlsCipherSuite.PskAes128Ccm8, DtlsCipherSuite.PskAes128GcmSha256]);

    public override int GetHandshakeTimeoutMillis() => (int)options.HandshakeTimeout.TotalMilliseconds;

    public int NegotiatedSuite => m_context.SecurityParameters.CipherSuite;
}

internal sealed class IoTComCertificateClient(TlsCrypto crypto, DtlsClientOptions options) : DefaultTlsClient(crypto)
{
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    protected override int[] GetSupportedCipherSuites() => DtlsBc.Suites(options.CipherSuites.Count > 0 ? options.CipherSuites : [DtlsCipherSuite.EcdheEcdsaAes128Ccm8, DtlsCipherSuite.EcdheEcdsaAes128GcmSha256]);

    public override int GetHandshakeTimeoutMillis() => (int)options.HandshakeTimeout.TotalMilliseconds;

    public override TlsAuthentication GetAuthentication() => new ServerAuthentication(options);

    public int NegotiatedSuite => m_context.SecurityParameters.CipherSuite;

    private sealed class ServerAuthentication(DtlsClientOptions options) : ServerOnlyTlsAuthentication
    {
        public override void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain is null || chain.IsEmpty) throw new TlsFatalAlert(AlertDescription.bad_certificate);
            using var leaf = X509CertificateLoader.LoadCertificate(chain.GetCertificateAt(0).GetEncoded());
            DtlsBc.Validate(leaf, options);
        }
    }
}

/// <summary>A DTLS 1.2 server for PSK and/or ECDHE_ECDSA; one instance per peer.</summary>
internal sealed class IoTComServer(TlsCrypto crypto, DtlsServerOptions options, PskManager psk)
    : PskTlsServer(crypto, psk)
{
    public PskManager Psk { get; } = psk;

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    protected override int[] GetSupportedCipherSuites()
    {
        if (options.CipherSuites.Count > 0) return DtlsBc.Suites(options.CipherSuites);
        var list = new List<DtlsCipherSuite>();
        if (options.PskLookup is not null) list.AddRange([DtlsCipherSuite.PskAes128Ccm8, DtlsCipherSuite.PskAes128GcmSha256]);
        if (options.Certificate is not null) list.AddRange([DtlsCipherSuite.EcdheEcdsaAes128Ccm8, DtlsCipherSuite.EcdheEcdsaAes128GcmSha256]);
        return DtlsBc.Suites(list);
    }

    public override int GetHandshakeTimeoutMillis() => (int)options.HandshakeTimeout.TotalMilliseconds;

    public override TlsCredentials GetCredentials()
    {
        var exchange = TlsUtilities.GetKeyExchangeAlgorithm(m_context.SecurityParameters.CipherSuite);
        if (exchange != KeyExchangeAlgorithm.ECDHE_ECDSA) return base.GetCredentials();
        var cert = options.Certificate ?? throw new TlsFatalAlert(AlertDescription.internal_error);
        using var ecdsa = cert.GetECDsaPrivateKey() ?? throw new TlsFatalAlert(AlertDescription.internal_error);
        var key = (ECPrivateKeyParameters)PrivateKeyFactory.CreateKey(ecdsa.ExportPkcs8PrivateKey());
        var bcCert = new Certificate([Crypto.CreateCertificate(cert.RawData)]);
        return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), (BcTlsCrypto)Crypto, key, bcCert,
            new SignatureAndHashAlgorithm(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
    }

    public int NegotiatedSuite => m_context.SecurityParameters.CipherSuite;
}

internal sealed class PskManager(Func<string, byte[]?>? lookup) : TlsPskIdentityManager
{
    public string? Identity { get; private set; }

    public byte[]? GetHint() => null;

    public byte[]? GetPsk(byte[] identity)
    {
        var text = Encoding.UTF8.GetString(identity);
        var key = lookup?.Invoke(text);
        if (key is not null) Identity = text;
        return key;
    }
}

internal static class EndPointBytes
{
    public static byte[] Of(EndPoint ep) => Encoding.UTF8.GetBytes(ep.ToString() ?? "");
}
