using System.Buffers.Binary;
using System.Globalization;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>LoRaWAN message type (MHDR bits 7..5).</summary>
public enum LoRaWanMType : byte
{
    /// <summary>Join-Request (device → network).</summary>
    JoinRequest = 0,
    /// <summary>Join-Accept (network → device, encrypted).</summary>
    JoinAccept = 1,
    /// <summary>Unconfirmed data uplink.</summary>
    UnconfirmedDataUp = 2,
    /// <summary>Unconfirmed data downlink.</summary>
    UnconfirmedDataDown = 3,
    /// <summary>Confirmed data uplink (the network answers with ACK).</summary>
    ConfirmedDataUp = 4,
    /// <summary>Confirmed data downlink (the device answers with ACK).</summary>
    ConfirmedDataDown = 5,
    /// <summary>Rejoin-Request (LoRaWAN 1.1; decoded structurally only).</summary>
    RejoinRequest = 6,
    /// <summary>Proprietary.</summary>
    Proprietary = 7,
}

/// <summary>
/// A 64-bit extended unique identifier (DevEUI, JoinEUI/AppEUI, gateway EUI). Written most significant byte first
/// (<c>70B3D57ED0000001</c>) and transmitted little-endian on air.
/// </summary>
/// <param name="Value">The EUI as a number.</param>
public readonly record struct Eui64(ulong Value)
{
    /// <summary>Parses 16 hex digits; separators <c>-</c> and <c>:</c> are ignored.</summary>
    public static Eui64 Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hex = text.Replace("-", "", StringComparison.Ordinal).Replace(":", "", StringComparison.Ordinal).Trim();
        if (hex.Length != 16) throw new FormatException($"An EUI-64 has 16 hex digits: '{text}'.");
        return new Eui64(ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>Reads an EUI transmitted little-endian.</summary>
    public static Eui64 ReadLittleEndian(ReadOnlySpan<byte> source) => new(BinaryPrimitives.ReadUInt64LittleEndian(source));

    /// <summary>Writes the EUI little-endian (on-air order).</summary>
    public void WriteLittleEndian(Span<byte> destination) => BinaryPrimitives.WriteUInt64LittleEndian(destination, Value);

    /// <summary>Reads an EUI written most significant byte first (Semtech UDP gateway header).</summary>
    public static Eui64 ReadBigEndian(ReadOnlySpan<byte> source) => new(BinaryPrimitives.ReadUInt64BigEndian(source));

    /// <summary>Writes the EUI most significant byte first.</summary>
    public void WriteBigEndian(Span<byte> destination) => BinaryPrimitives.WriteUInt64BigEndian(destination, Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("X16", CultureInfo.InvariantCulture);
}

/// <summary>A 32-bit device address: NwkID (top 7 bits) and NwkAddr. Transmitted little-endian.</summary>
/// <param name="Value">The address as a number.</param>
public readonly record struct DevAddr(uint Value)
{
    /// <summary>Parses 8 hex digits.</summary>
    public static DevAddr Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hex = text.Trim();
        if (hex.Length != 8) throw new FormatException($"A DevAddr has 8 hex digits: '{text}'.");
        return new DevAddr(uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>The network identifier (7 most significant bits).</summary>
    public byte NwkId => (byte)(Value >> 25);

    /// <summary>Reads an address transmitted little-endian.</summary>
    public static DevAddr ReadLittleEndian(ReadOnlySpan<byte> source) => new(BinaryPrimitives.ReadUInt32LittleEndian(source));

    /// <summary>Writes the address little-endian (on-air order).</summary>
    public void WriteLittleEndian(Span<byte> destination) => BinaryPrimitives.WriteUInt32LittleEndian(destination, Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("X8", CultureInfo.InvariantCulture);
}

/// <summary>Session keys of an activated device (LoRaWAN 1.0.x).</summary>
/// <param name="NwkSKey">Network session key: MIC and MAC commands in FRMPayload (FPort 0).</param>
/// <param name="AppSKey">Application session key: encrypts application payloads (FPort 1..223).</param>
public sealed record LoRaWanSessionKeys(byte[] NwkSKey, byte[] AppSKey)
{
    /// <summary>Creates keys from hex strings.</summary>
    public static LoRaWanSessionKeys FromHex(string nwkSKey, string appSKey) => new(LoRaWanKeys.Parse(nwkSKey), LoRaWanKeys.Parse(appSKey));

    /// <summary>Returns the key that encrypts FRMPayload on <paramref name="fport"/>.</summary>
    public byte[] PayloadKey(byte fport) => fport == 0 ? NwkSKey : AppSKey;

    /// <inheritdoc />
    public override string ToString() => "LoRaWanSessionKeys { NwkSKey = ****, AppSKey = **** }";
}

/// <summary>Helpers for 128-bit keys.</summary>
public static class LoRaWanKeys
{
    /// <summary>Parses a 32-digit hex key.</summary>
    public static byte[] Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var bytes = Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal));
        if (bytes.Length != 16) throw new FormatException("A LoRaWAN key has 16 bytes (32 hex digits).");
        return bytes;
    }

    /// <summary>A random key (for simulators and provisioning).</summary>
    public static byte[] Random() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
}

/// <summary>The FCtrl byte of a data frame.</summary>
/// <param name="Value">Raw byte.</param>
/// <param name="Uplink">Direction (bit 4 means ClassB uplink, FPending downlink).</param>
public readonly record struct LoRaWanFCtrl(byte Value, bool Uplink)
{
    /// <summary>Builds an FCtrl byte.</summary>
    public static LoRaWanFCtrl Create(bool uplink, bool adr = false, bool adrAckReq = false, bool ack = false, bool fPendingOrClassB = false, int fOptsLength = 0)
    {
        if (fOptsLength is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(fOptsLength), "FOpts holds at most 15 bytes.");
        var v = (adr ? 0x80 : 0) | (adrAckReq && uplink ? 0x40 : 0) | (ack ? 0x20 : 0) | (fPendingOrClassB ? 0x10 : 0) | fOptsLength;
        return new LoRaWanFCtrl((byte)v, uplink);
    }

    /// <summary>Adaptive data rate.</summary>
    public bool Adr => (Value & 0x80) != 0;

    /// <summary>ADR acknowledgement request (uplink only).</summary>
    public bool AdrAckReq => Uplink && (Value & 0x40) != 0;

    /// <summary>Acknowledges the last confirmed frame.</summary>
    public bool Ack => (Value & 0x20) != 0;

    /// <summary>Downlink: the network has more data pending.</summary>
    public bool FPending => !Uplink && (Value & 0x10) != 0;

    /// <summary>Uplink: the device is in Class B.</summary>
    public bool ClassB => Uplink && (Value & 0x10) != 0;

    /// <summary>Length of FOpts (0..15).</summary>
    public int FOptsLength => Value & 0x0F;

    /// <inheritdoc />
    public override string ToString()
    {
        var flags = new List<string>(4);
        if (Adr) flags.Add("ADR");
        if (AdrAckReq) flags.Add("ADRACKReq");
        if (Ack) flags.Add("ACK");
        if (FPending) flags.Add("FPending");
        if (ClassB) flags.Add("ClassB");
        if (FOptsLength > 0) flags.Add($"FOptsLen={FOptsLength}");
        return flags.Count == 0 ? "-" : string.Join(' ', flags);
    }
}
