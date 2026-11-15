using System.Security.Cryptography;
using Org.BouncyCastle.Math;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>
/// SPAKE2+ over P-256 with SHA-256, HKDF and HMAC as Matter's PASE uses it (core spec §3.10): the commissioner (prover)
/// knows the passcode, the device (verifier) stores only w0 and L = w1·G. Both sides end with Ke, from which the PASE
/// session keys are derived; a wrong passcode makes the confirmation MACs differ.
/// </summary>
public static class Spake2Plus
{
    /// <summary>Context prefix hashed with the PBKDFParamRequest and PBKDFParamResponse payloads.</summary>
    public const string ContextPrefix = "CHIP PAKE V1 Commissioning";

    internal static readonly ECPoint M = MatterCrypto.DecodePoint(Convert.FromHexString("02886E2F97ACE46E55BA9DD7242579F2993B64E16EF3DCAB95AFD497333D8FA12F"));
    internal static readonly ECPoint N = MatterCrypto.DecodePoint(Convert.FromHexString("03D8BBD6C639C62937B04D997F38C3770719C629D7014D49A24B4F98BAA1292B49"));

    /// <summary>w0 and w1 from the passcode (PBKDF2 over the 4-byte little-endian passcode, 80 bytes, each half mod n).</summary>
    public static (byte[] W0, byte[] W1) ComputeW(uint passcode, byte[] salt, int iterations)
    {
        if (salt.Length is < 16 or > 32) throw new ArgumentException("The PBKDF salt has 16–32 bytes.", nameof(salt));
        if (iterations is < 1000 or > 100000) throw new ArgumentException("PBKDF iterations are 1000–100000.", nameof(iterations));
        var ws = MatterCrypto.Pbkdf2(BitConverter.GetBytes(passcode), salt, iterations, 80);
        return (MatterCrypto.ScalarBytes(MatterCrypto.Scalar(ws.AsSpan(0, 40))), MatterCrypto.ScalarBytes(MatterCrypto.Scalar(ws.AsSpan(40, 40))));
    }

    /// <summary>The verifier stored on a device: w0 (32 bytes) ‖ L (65 bytes).</summary>
    public static byte[] ComputeVerifier(uint passcode, byte[] salt, int iterations)
    {
        var (w0, w1) = ComputeW(passcode, salt, iterations);
        return [.. w0, .. MatterCrypto.Encode(MatterCrypto.G.Multiply(new BigInteger(1, w1)))];
    }

    /// <summary>The context hash for the transcript.</summary>
    public static byte[] Context(byte[] pbkdfParamRequest, byte[] pbkdfParamResponse)
        => MatterCrypto.Sha256(System.Text.Encoding.ASCII.GetBytes(ContextPrefix), pbkdfParamRequest, pbkdfParamResponse);

    internal static (byte[] Ke, byte[] Kca, byte[] Kcb) Keys(byte[] context, ECPoint x, ECPoint y, ECPoint z, ECPoint v, BigInteger w0)
    {
        var tt = new List<byte>();
        void Add(byte[] item)
        {
            tt.AddRange(BitConverter.GetBytes((ulong)item.Length));
            tt.AddRange(item);
        }

        Add(context);
        Add([]);   // prover identity
        Add([]);   // verifier identity
        Add(MatterCrypto.Encode(M));
        Add(MatterCrypto.Encode(N));
        Add(MatterCrypto.Encode(x));
        Add(MatterCrypto.Encode(y));
        Add(MatterCrypto.Encode(z));
        Add(MatterCrypto.Encode(v));
        Add(MatterCrypto.ScalarBytes(w0));
        var k = MatterCrypto.Sha256([.. tt]);
        var ka = k[..16];
        var kc = MatterCrypto.Hkdf(ka, [], "ConfirmationKeys", 32);
        return (k[16..], kc[..16], kc[16..]);
    }

    /// <summary>PASE session keys from Ke: I2R key, R2I key, attestation challenge (16 bytes each).</summary>
    public static (byte[] I2R, byte[] R2I, byte[] AttestationChallenge) SessionKeys(byte[] ke)
    {
        var k = MatterCrypto.Hkdf(ke, [], "SessionKeys", 48);
        return (k[..16], k[16..32], k[32..]);
    }
}

/// <summary>The commissioner's side of SPAKE2+.</summary>
public sealed class Spake2PlusProver
{
    private readonly byte[] _context;
    private readonly BigInteger _w0, _w1, _x;

    /// <summary>Starts with w0, w1 and the context hash (<paramref name="x"/> fixes the random scalar for test vectors).</summary>
    public Spake2PlusProver(byte[] context, byte[] w0, byte[] w1, byte[]? x = null)
    {
        (_context, _w0, _w1) = (context, new BigInteger(1, w0), new BigInteger(1, w1));
        _x = x is null ? MatterCrypto.RandomScalar() : new BigInteger(1, x);
        X = MatterCrypto.Encode(MatterCrypto.G.Multiply(_x).Add(Spake2Plus.M.Multiply(_w0)));
    }

    /// <summary>pA (Pake1).</summary>
    public byte[] X { get; }

    /// <summary>Ke after a successful <see cref="Finish"/>.</summary>
    public byte[]? Ke { get; private set; }

    /// <summary>Processes pB and cB (Pake2); returns cA (Pake3).</summary>
    /// <exception cref="ProtocolException">cB does not verify: the device has a different passcode.</exception>
    public byte[] Finish(byte[] pB, byte[] cB)
    {
        var y = MatterCrypto.DecodePoint(pB);
        var t = y.Subtract(Spake2Plus.N.Multiply(_w0));
        var z = t.Multiply(_x);
        var v = t.Multiply(_w1);
        var (ke, kca, kcb) = Spake2Plus.Keys(_context, MatterCrypto.DecodePoint(X), y, z, v, _w0);
        if (!CryptographicOperations.FixedTimeEquals(MatterCrypto.Hmac(kcb, X), cB)) throw new ProtocolException("PASE confirmation failed: wrong setup passcode.");
        Ke = ke;
        return MatterCrypto.Hmac(kca, pB);
    }
}

/// <summary>The device's side of SPAKE2+.</summary>
public sealed class Spake2PlusVerifier
{
    private readonly byte[] _context;
    private readonly BigInteger _w0, _y;
    private readonly ECPoint _l;
    private byte[]? _expectedCa, _ke;

    /// <summary>Starts from the stored verifier (w0 ‖ L) and the context hash.</summary>
    public Spake2PlusVerifier(byte[] context, byte[] verifier, byte[]? y = null)
    {
        if (verifier.Length != 32 + MatterCrypto.PointLength) throw new ArgumentException("A SPAKE2+ verifier is w0 (32) ‖ L (65).", nameof(verifier));
        _context = context;
        _w0 = new BigInteger(1, verifier[..32]);
        _l = MatterCrypto.DecodePoint(verifier.AsSpan(32));
        _y = y is null ? MatterCrypto.RandomScalar() : new BigInteger(1, y);
        Y = MatterCrypto.Encode(MatterCrypto.G.Multiply(_y).Add(Spake2Plus.N.Multiply(_w0)));
    }

    /// <summary>pB (Pake2).</summary>
    public byte[] Y { get; }

    /// <summary>Processes pA (Pake1); returns cB.</summary>
    public byte[] Respond(byte[] pA)
    {
        var x = MatterCrypto.DecodePoint(pA);
        var z = x.Subtract(Spake2Plus.M.Multiply(_w0)).Multiply(_y);
        var v = _l.Multiply(_y);
        var (ke, kca, kcb) = Spake2Plus.Keys(_context, x, MatterCrypto.DecodePoint(Y), z, v, _w0);
        (_ke, _expectedCa) = (ke, MatterCrypto.Hmac(kca, Y));
        return MatterCrypto.Hmac(kcb, pA);
    }

    /// <summary>Checks cA (Pake3) and returns Ke.</summary>
    /// <exception cref="ProtocolException">cA does not verify.</exception>
    public byte[] Confirm(byte[] cA)
    {
        if (_expectedCa is null || !CryptographicOperations.FixedTimeEquals(_expectedCa, cA)) throw new ProtocolException("PASE confirmation failed: wrong setup passcode.");
        return _ke!;
    }
}
