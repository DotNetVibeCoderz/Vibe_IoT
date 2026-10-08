using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>
/// LoRaWAN 1.0.x cryptography over the BCL's AES: AES-CMAC (RFC 4493), message integrity codes, FRMPayload
/// encryption, Join-Accept encryption and session key derivation.
/// </summary>
public static class LoRaWanCrypto
{
    /// <summary>AES-128 encryption of one block (ECB).</summary>
    public static void AesEncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block, Span<byte> destination)
    {
        using var aes = CreateAes(key);
        aes.EncryptEcb(block[..16], destination[..16], PaddingMode.None);
    }

    /// <summary>AES-CMAC (RFC 4493) of <paramref name="message"/>: a 16-byte tag.</summary>
    public static byte[] AesCmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        using var aes = CreateAes(key);
        Span<byte> l = stackalloc byte[16];
        Span<byte> zero = stackalloc byte[16];
        zero.Clear();
        aes.EncryptEcb(zero, l, PaddingMode.None);
        Span<byte> k1 = stackalloc byte[16];
        Span<byte> k2 = stackalloc byte[16];
        ShiftLeftXor(l, k1);
        ShiftLeftXor(k1, k2);

        var blocks = Math.Max(1, (message.Length + 15) / 16);
        var complete = message.Length > 0 && message.Length % 16 == 0;
        Span<byte> x = stackalloc byte[16];
        Span<byte> y = stackalloc byte[16];
        x.Clear();
        for (var i = 0; i < blocks - 1; i++)
        {
            var block = message.Slice(i * 16, 16);
            for (var j = 0; j < 16; j++) y[j] = (byte)(x[j] ^ block[j]);
            aes.EncryptEcb(y, x, PaddingMode.None);
        }

        Span<byte> last = stackalloc byte[16];
        last.Clear();
        var tail = message[((blocks - 1) * 16)..];
        tail.CopyTo(last);
        if (complete)
        {
            for (var j = 0; j < 16; j++) last[j] ^= k1[j];
        }
        else
        {
            last[tail.Length] = 0x80;
            for (var j = 0; j < 16; j++) last[j] ^= k2[j];
        }

        for (var j = 0; j < 16; j++) y[j] = (byte)(x[j] ^ last[j]);
        var tag = new byte[16];
        aes.EncryptEcb(y, tag, PaddingMode.None);
        return tag;
    }

    /// <summary>MIC of a Join-Request or a (plaintext) Join-Accept: the first 4 bytes of AES-CMAC(AppKey, message).</summary>
    public static uint ComputeJoinMic(ReadOnlySpan<byte> appKey, ReadOnlySpan<byte> messageWithoutMic) =>
        BinaryPrimitives.ReadUInt32LittleEndian(AesCmac(appKey, messageWithoutMic));

    /// <summary>MIC of a data frame (B0 block + MHDR..FRMPayload) with the full 32-bit frame counter.</summary>
    public static uint ComputeDataMic(ReadOnlySpan<byte> nwkSKey, bool uplink, DevAddr devAddr, uint fCnt, ReadOnlySpan<byte> messageWithoutMic)
    {
        Span<byte> buffer = messageWithoutMic.Length + 16 <= 512 ? stackalloc byte[messageWithoutMic.Length + 16] : new byte[messageWithoutMic.Length + 16];
        buffer[..16].Clear();
        buffer[0] = 0x49;
        buffer[5] = uplink ? (byte)0 : (byte)1;
        devAddr.WriteLittleEndian(buffer[6..]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[10..], fCnt);
        buffer[15] = (byte)messageWithoutMic.Length;
        messageWithoutMic.CopyTo(buffer[16..]);
        return BinaryPrimitives.ReadUInt32LittleEndian(AesCmac(nwkSKey, buffer));
    }

    /// <summary>
    /// Encrypts or decrypts FRMPayload (the operation is its own inverse): XOR with AES(K, A_i), where A_i carries the
    /// direction, DevAddr, the full frame counter and the block index.
    /// </summary>
    public static byte[] CryptPayload(ReadOnlySpan<byte> key, bool uplink, DevAddr devAddr, uint fCnt, ReadOnlySpan<byte> payload)
    {
        var result = payload.ToArray();
        if (result.Length == 0) return result;
        using var aes = CreateAes(key);
        Span<byte> a = stackalloc byte[16];
        Span<byte> s = stackalloc byte[16];
        a.Clear();
        a[0] = 0x01;
        a[5] = uplink ? (byte)0 : (byte)1;
        devAddr.WriteLittleEndian(a[6..]);
        BinaryPrimitives.WriteUInt32LittleEndian(a[10..], fCnt);
        for (var i = 0; i * 16 < result.Length; i++)
        {
            a[15] = (byte)(i + 1);
            aes.EncryptEcb(a, s, PaddingMode.None);
            var n = Math.Min(16, result.Length - (i * 16));
            for (var j = 0; j < n; j++) result[(i * 16) + j] ^= s[j];
        }

        return result;
    }

    /// <summary>
    /// Encrypts a Join-Accept as the network does: AES <em>decryption</em> of everything after MHDR (including the MIC),
    /// so that the device only needs AES encryption to read it.
    /// </summary>
    public static byte[] EncryptJoinAccept(ReadOnlySpan<byte> appKey, ReadOnlySpan<byte> payloadAndMic)
    {
        if (payloadAndMic.Length % 16 != 0) throw new ArgumentException("The Join-Accept body must be 16 or 32 bytes.", nameof(payloadAndMic));
        using var aes = CreateAes(appKey);
        return aes.DecryptEcb(payloadAndMic, PaddingMode.None);
    }

    /// <summary>Decrypts a Join-Accept on the device side (AES encryption of everything after MHDR).</summary>
    public static byte[] DecryptJoinAccept(ReadOnlySpan<byte> appKey, ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length % 16 != 0) throw new ArgumentException("The Join-Accept body must be 16 or 32 bytes.", nameof(encrypted));
        using var aes = CreateAes(appKey);
        return aes.EncryptEcb(encrypted, PaddingMode.None);
    }

    /// <summary>
    /// Derives the 1.0.x session keys: <c>aes128_encrypt(AppKey, 0x01|0x02 | JoinNonce | NetID | DevNonce | pad16)</c>.
    /// </summary>
    public static LoRaWanSessionKeys DeriveSessionKeys(ReadOnlySpan<byte> appKey, uint joinNonce, uint netId, ushort devNonce)
    {
        Span<byte> block = stackalloc byte[16];
        var nwk = new byte[16];
        var app = new byte[16];
        Fill(block, 0x01, joinNonce, netId, devNonce);
        AesEncryptBlock(appKey, block, nwk);
        Fill(block, 0x02, joinNonce, netId, devNonce);
        AesEncryptBlock(appKey, block, app);
        return new LoRaWanSessionKeys(nwk, app);

        static void Fill(Span<byte> b, byte kind, uint joinNonce, uint netId, ushort devNonce)
        {
            b.Clear();
            b[0] = kind;
            WriteUInt24(b[1..], joinNonce);
            WriteUInt24(b[4..], netId);
            BinaryPrimitives.WriteUInt16LittleEndian(b[7..], devNonce);
        }
    }

    internal static void WriteUInt24(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    internal static uint ReadUInt24(ReadOnlySpan<byte> source) => (uint)(source[0] | (source[1] << 8) | (source[2] << 16));

    private static Aes CreateAes(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16) throw new ArgumentException("LoRaWAN keys are 16 bytes.", nameof(key));
        var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes;
    }

    private static void ShiftLeftXor(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var carry = 0;
        for (var i = 15; i >= 0; i--)
        {
            var b = input[i];
            output[i] = (byte)((b << 1) | carry);
            carry = b >> 7;
        }

        if ((input[0] & 0x80) != 0) output[15] ^= 0x87;
    }
}
