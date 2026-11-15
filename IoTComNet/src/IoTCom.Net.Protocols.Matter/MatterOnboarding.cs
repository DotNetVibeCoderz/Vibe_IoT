using System.Globalization;
using System.Text;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>How a device is put into commissioning mode.</summary>
public enum CommissioningFlow : byte
{
    /// <summary>Commissionable when powered on.</summary>
    Standard = 0,
    /// <summary>A user action (button press) is needed.</summary>
    UserIntent = 1,
    /// <summary>Vendor-specific steps.</summary>
    Custom = 2,
}

/// <summary>Transports the device announces itself on.</summary>
[Flags]
public enum DiscoveryCapabilities : byte
{
    /// <summary>None given.</summary>
    None = 0,
    /// <summary>SoftAP.</summary>
    SoftAp = 1,
    /// <summary>Bluetooth LE.</summary>
    Ble = 2,
    /// <summary>Already on the IP network.</summary>
    OnNetwork = 4,
    /// <summary>Wi-Fi Public Action Frame.</summary>
    WiFiPaf = 8,
}

/// <summary>
/// A Matter onboarding payload (core spec §5.1): what a QR code (<c>MT:…</c>) and the 11/21-digit manual pairing code
/// carry — vendor and product ids, commissioning flow, discovery capabilities, the 12-bit discriminator and the setup
/// passcode.
/// </summary>
/// <param name="VendorId">Vendor id (0xFFF1–0xFFF4 are test vendors).</param>
/// <param name="ProductId">Product id.</param>
/// <param name="Flow">Commissioning flow.</param>
/// <param name="Discovery">Discovery capabilities.</param>
/// <param name="Discriminator">12-bit discriminator.</param>
/// <param name="Passcode">27-bit setup passcode (1–99999998, not one of the trivial values).</param>
public sealed record MatterSetupPayload(ushort VendorId, ushort ProductId, CommissioningFlow Flow, DiscoveryCapabilities Discovery, ushort Discriminator, uint Passcode)
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-.";

    private static readonly uint[] Invalid = [0, 11111111, 22222222, 33333333, 44444444, 55555555, 66666666, 77777777, 88888888, 99999999, 12345678, 87654321];

    /// <summary>True when the passcode is allowed by the specification.</summary>
    public static bool IsValidPasscode(uint passcode) => passcode is >= 1 and <= 99999998 && !Invalid.Contains(passcode);

    /// <summary>The 4-bit short discriminator used by the manual code and BLE advertisements.</summary>
    public int ShortDiscriminator => Discriminator >> 8;

    /// <summary>Encodes the QR code text (<c>MT:</c> + base38 of the 88-bit packed payload).</summary>
    public string ToQrCode()
    {
        if (Discriminator > 0xFFF) throw new ArgumentOutOfRangeException(nameof(Discriminator), "The discriminator has 12 bits.");
        if (!IsValidPasscode(Passcode)) throw new ArgumentOutOfRangeException(nameof(Passcode), "The passcode is outside 1–99999998 or a trivial value.");
        var bits = new byte[11];
        var at = 0;
        void Put(ulong value, int count)
        {
            for (var i = 0; i < count; i++, at++)
                if (((value >> i) & 1) != 0) bits[at / 8] |= (byte)(1 << (at % 8));
        }

        Put(0, 3);   // version
        Put(VendorId, 16);
        Put(ProductId, 16);
        Put((ulong)Flow, 2);
        Put((ulong)Discovery, 8);
        Put(Discriminator, 12);
        Put(Passcode, 27);
        Put(0, 4);   // padding
        return "MT:" + Base38Encode(bits);
    }

    /// <summary>Decodes QR code text.</summary>
    /// <exception cref="ProtocolException">Not an <c>MT:</c> payload, bad base38, unsupported version.</exception>
    public static MatterSetupPayload FromQrCode(string text)
    {
        if (!text.StartsWith("MT:", StringComparison.Ordinal)) throw new ProtocolException("A Matter QR payload starts with 'MT:'.");
        var body = text[3..];
        var star = body.IndexOf('*');
        if (star >= 0) body = body[..star];   // concatenated payloads: the first one
        var bits = Base38Decode(body);
        if (bits.Length < 11) throw new ProtocolException($"A Matter QR payload has at least 11 bytes; got {bits.Length}.");
        var at = 0;
        ulong Get(int count)
        {
            ulong v = 0;
            for (var i = 0; i < count; i++, at++)
                if ((bits[at / 8] & (1 << (at % 8))) != 0) v |= 1UL << i;
            return v;
        }

        if (Get(3) != 0) throw new ProtocolException("Unsupported Matter QR payload version.");
        var vid = (ushort)Get(16);
        var pid = (ushort)Get(16);
        var flow = (CommissioningFlow)Get(2);
        var disc = (DiscoveryCapabilities)Get(8);
        var discriminator = (ushort)Get(12);
        var passcode = (uint)Get(27);
        if (!IsValidPasscode(passcode)) throw new ProtocolException("The QR payload carries an invalid passcode.");
        return new MatterSetupPayload(vid, pid, flow, disc, discriminator, passcode);
    }

    /// <summary>
    /// The manual pairing code: 11 digits, or 21 with vendor and product ids (when the flow is not standard), ending in a
    /// Verhoeff check digit.
    /// </summary>
    public string ToManualCode()
    {
        if (!IsValidPasscode(Passcode)) throw new ArgumentOutOfRangeException(nameof(Passcode), "The passcode is outside 1–99999998 or a trivial value.");
        var vidPid = Flow != CommissioningFlow.Standard;
        var shortDisc = ShortDiscriminator;
        var chunk1 = ((vidPid ? 1 : 0) << 2) | (shortDisc >> 2);
        var chunk2 = ((shortDisc & 0x3) << 14) | (int)(Passcode & 0x3FFF);
        var chunk3 = (int)(Passcode >> 14);
        var digits = string.Create(CultureInfo.InvariantCulture, $"{chunk1}{chunk2:D5}{chunk3:D4}");
        if (vidPid) digits += string.Create(CultureInfo.InvariantCulture, $"{VendorId:D5}{ProductId:D5}");
        return digits + Verhoeff.Compute(digits);
    }

    /// <summary>
    /// Decodes a manual pairing code (dashes and spaces ignored). The 11-digit form carries only the short discriminator
    /// (its top 4 bits, shifted into place) and the passcode.
    /// </summary>
    public static MatterSetupPayload FromManualCode(string code)
    {
        var digits = new string([.. code.Where(char.IsDigit)]);
        if (digits.Length is not (11 or 21)) throw new ProtocolException("A manual pairing code has 11 or 21 digits.");
        if (!Verhoeff.Validate(digits)) throw new ProtocolException("The manual pairing code's check digit is wrong.");
        var chunk1 = digits[0] - '0';
        var vidPid = (chunk1 & 0x4) != 0;
        if (vidPid != (digits.Length == 21)) throw new ProtocolException("The manual pairing code's length does not match its vendor/product flag.");
        var chunk2 = int.Parse(digits.AsSpan(1, 5), CultureInfo.InvariantCulture);
        var chunk3 = int.Parse(digits.AsSpan(6, 4), CultureInfo.InvariantCulture);
        var shortDisc = ((chunk1 & 0x3) << 2) | (chunk2 >> 14);
        var passcode = (uint)((chunk3 << 14) | (chunk2 & 0x3FFF));
        if (!IsValidPasscode(passcode)) throw new ProtocolException("The manual pairing code carries an invalid passcode.");
        ushort vid = 0, pid = 0;
        if (vidPid)
        {
            vid = ushort.Parse(digits.AsSpan(10, 5), CultureInfo.InvariantCulture);
            pid = ushort.Parse(digits.AsSpan(15, 5), CultureInfo.InvariantCulture);
        }

        return new MatterSetupPayload(vid, pid, vidPid ? CommissioningFlow.UserIntent : CommissioningFlow.Standard, DiscoveryCapabilities.None, (ushort)(shortDisc << 8), passcode);
    }

    /// <summary>Base38 (core spec §5.1.3): 3 bytes → 5 characters, 2 → 4, 1 → 2, little-endian.</summary>
    public static string Base38Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i += 3)
        {
            var n = Math.Min(3, data.Length - i);
            uint v = 0;
            for (var k = 0; k < n; k++) v |= (uint)data[i + k] << (8 * k);
            var chars = n switch { 3 => 5, 2 => 4, _ => 2 };
            for (var c = 0; c < chars; c++)
            {
                sb.Append(Alphabet[(int)(v % 38)]);
                v /= 38;
            }
        }

        return sb.ToString();
    }

    /// <summary>Decodes base38.</summary>
    public static byte[] Base38Decode(string text)
    {
        var result = new List<byte>();
        for (var i = 0; i < text.Length;)
        {
            var remaining = text.Length - i;
            var chars = remaining >= 5 ? 5 : remaining switch { 4 => 4, 2 => 2, _ => throw new ProtocolException($"Base38 text ends with {remaining} characters.") };
            ulong v = 0;
            for (var c = chars - 1; c >= 0; c--)
            {
                var idx = Alphabet.IndexOf(text[i + c], StringComparison.Ordinal);
                if (idx < 0) throw new ProtocolException($"'{text[i + c]}' is not a base38 character.");
                v = (v * 38) + (ulong)idx;
            }

            var bytes = chars switch { 5 => 3, 4 => 2, _ => 1 };
            if (v >> (8 * bytes) != 0) throw new ProtocolException("Base38 chunk out of range.");
            for (var k = 0; k < bytes; k++) result.Add((byte)(v >> (8 * k)));
            i += chars;
        }

        return [.. result];
    }

    /// <summary>Normalised description.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"vid={VendorId:X4} pid={ProductId:X4} flow={(int)Flow} disc={(int)Discovery} discriminator={Discriminator} passcode={Passcode}");
}

/// <summary>The Verhoeff check digit (dihedral group D5), as used by the manual pairing code.</summary>
public static class Verhoeff
{
    private static readonly int[,] D =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 }, { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 },
        { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 }, { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 },
        { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 }, { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly int[,] P =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 }, { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 },
        { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 }, { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    private static readonly int[] Inv = [0, 4, 3, 2, 1, 5, 6, 7, 8, 9];

    /// <summary>The check digit for <paramref name="digits"/>.</summary>
    public static char Compute(string digits)
    {
        var c = 0;
        for (var i = 0; i < digits.Length; i++) c = D[c, P[(i + 1) % 8, digits[digits.Length - 1 - i] - '0']];
        return (char)('0' + Inv[c]);
    }

    /// <summary>True when the last digit is the check digit of the others.</summary>
    public static bool Validate(string digitsWithCheck)
    {
        var c = 0;
        for (var i = 0; i < digitsWithCheck.Length; i++) c = D[c, P[i % 8, digitsWithCheck[digitsWithCheck.Length - 1 - i] - '0']];
        return c == 0;
    }
}
