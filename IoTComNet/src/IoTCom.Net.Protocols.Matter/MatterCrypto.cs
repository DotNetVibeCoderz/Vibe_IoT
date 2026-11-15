using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;
using ECCurve = System.Security.Cryptography.ECCurve;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>
/// The Matter cryptographic primitives (core spec chapter 3): SHA-256, HMAC, HKDF, PBKDF2, AES-CCM-128 with a 13-byte
/// nonce and 16-byte tag, and P-256 point arithmetic, ECDH and ECDSA. AES-CCM and the EC math come from BouncyCastle so
/// they behave the same on every OS (the BCL's AesCcm is not available on macOS).
/// </summary>
public static class MatterCrypto
{
    /// <summary>The P-256 curve.</summary>
    internal static readonly X9ECParameters Curve = ECNamedCurveTable.GetByName("P-256");

    /// <summary>Group order.</summary>
    internal static BigInteger N => Curve.N;

    /// <summary>Generator.</summary>
    internal static ECPoint G => Curve.G;

    private static readonly SecureRandom Random = new();

    /// <summary>Length of an uncompressed P-256 point.</summary>
    public const int PointLength = 65;

    /// <summary>AES-CCM tag (MIC) length.</summary>
    public const int MicLength = 16;

    /// <summary>SHA-256.</summary>
    public static byte[] Sha256(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    /// <summary>HMAC-SHA256.</summary>
    public static byte[] Hmac(byte[] key, params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    /// <summary>HKDF-SHA256.</summary>
    public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, System.Text.Encoding.ASCII.GetBytes(info));

    /// <summary>PBKDF2-HMAC-SHA256.</summary>
    public static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int length)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);

    /// <summary>Random bytes.</summary>
    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary>AES-CCM-128 encrypt: ciphertext followed by the 16-byte MIC.</summary>
    public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
    {
        var ccm = new CcmBlockCipher(new AesEngine());
        ccm.Init(true, new AeadParameters(new KeyParameter(key), MicLength * 8, nonce, aad));
        var output = new byte[ccm.GetOutputSize(plaintext.Length)];
        var n = ccm.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        ccm.DoFinal(output, n);
        return output;
    }

    /// <summary>AES-CCM-128 decrypt and verify.</summary>
    /// <exception cref="ProtocolException">The MIC does not verify (wrong key, tampered message).</exception>
    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertextAndMic, byte[] aad)
    {
        if (ciphertextAndMic.Length < MicLength) throw new ProtocolException("Encrypted payload shorter than its MIC.");
        var ccm = new CcmBlockCipher(new AesEngine());
        ccm.Init(false, new AeadParameters(new KeyParameter(key), MicLength * 8, nonce, aad));
        var output = new byte[ccm.GetOutputSize(ciphertextAndMic.Length)];
        try
        {
            var n = ccm.ProcessBytes(ciphertextAndMic, 0, ciphertextAndMic.Length, output, 0);
            ccm.DoFinal(output, n);
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            throw new ProtocolException("Message integrity check failed.");
        }

        return output;
    }

    // ---- P-256

    /// <summary>Decodes an uncompressed or compressed point and checks it is on the curve.</summary>
    internal static ECPoint DecodePoint(ReadOnlySpan<byte> encoded)
    {
        try
        {
            var p = Curve.Curve.DecodePoint(encoded.ToArray());
            if (p.IsInfinity || !p.IsValid()) throw new ProtocolException("Point is not on P-256.");
            return p.Normalize();
        }
        catch (ArgumentException)
        {
            throw new ProtocolException("Point is not a valid P-256 encoding.");
        }
    }

    /// <summary>Uncompressed encoding (65 bytes).</summary>
    internal static byte[] Encode(ECPoint p) => p.Normalize().GetEncoded(false);

    /// <summary>A scalar from big-endian bytes, reduced modulo the order.</summary>
    internal static BigInteger Scalar(ReadOnlySpan<byte> bigEndian) => new BigInteger(1, bigEndian.ToArray()).Mod(N);

    /// <summary>A scalar as 32 big-endian bytes.</summary>
    internal static byte[] ScalarBytes(BigInteger s) => s.ToByteArrayUnsigned() is { Length: <= 32 } b ? [.. new byte[32 - b.Length], .. b] : throw new ArgumentException("Scalar too large.");

    /// <summary>A uniformly random scalar in [1, n-1].</summary>
    internal static BigInteger RandomScalar()
    {
        BigInteger k;
        do k = new BigInteger(N.BitLength, Random);
        while (k.SignValue == 0 || k.CompareTo(N) >= 0);
        return k;
    }

    /// <summary>A new P-256 key pair as (private 32 bytes, public 65 bytes).</summary>
    public static (byte[] Private, byte[] Public) GenerateKeyPair()
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        return (p.D!, [0x04, .. p.Q.X!, .. p.Q.Y!]);
    }

    /// <summary>ECDH shared secret (the x coordinate, 32 bytes).</summary>
    public static byte[] Ecdh(byte[] privateKey, byte[] peerPublic)
    {
        var shared = DecodePoint(peerPublic).Multiply(new BigInteger(1, privateKey)).Normalize();
        if (shared.IsInfinity) throw new ProtocolException("ECDH produced the point at infinity.");
        return ScalarBytes(shared.AffineXCoord.ToBigInteger());
    }

    private static ECDsa EcdsaFrom(byte[] privateKey)
    {
        var pub = G.Multiply(new BigInteger(1, privateKey)).Normalize();
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = new System.Security.Cryptography.ECPoint { X = ScalarBytes(pub.AffineXCoord.ToBigInteger()), Y = ScalarBytes(pub.AffineYCoord.ToBigInteger()) },
        });
    }

    /// <summary>ECDSA P-256 / SHA-256 signature as raw r‖s (64 bytes).</summary>
    public static byte[] Sign(byte[] privateKey, byte[] message)
    {
        using var ec = EcdsaFrom(privateKey);
        return ec.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Verifies a raw r‖s signature against an uncompressed public key.</summary>
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != PointLength || publicKey[0] != 0x04 || signature.Length != 64) return false;
        using var ec = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new System.Security.Cryptography.ECPoint { X = publicKey[1..33], Y = publicKey[33..] },
        });
        try
        {
            return ec.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The public key of a private key (uncompressed).</summary>
    public static byte[] PublicKeyOf(byte[] privateKey) => Encode(G.Multiply(new BigInteger(1, privateKey)));
}
