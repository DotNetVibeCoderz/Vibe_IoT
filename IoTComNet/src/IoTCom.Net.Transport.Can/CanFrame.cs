using System.Globalization;
using System.Text;

namespace IoTCom.Net.Transport.Can;

/// <summary>CAN frame flags.</summary>
[Flags]
public enum CanFrameFlags : byte
{
    /// <summary>Classic data frame with an 11-bit identifier.</summary>
    None = 0,
    /// <summary>29-bit (extended) identifier.</summary>
    Extended = 1,
    /// <summary>Remote transmission request (classic CAN only).</summary>
    Remote = 2,
    /// <summary>CAN FD frame (up to 64 data bytes).</summary>
    Fd = 4,
    /// <summary>CAN FD bit-rate switch: the data phase uses the data bit rate.</summary>
    BitRateSwitch = 8,
    /// <summary>CAN FD error-state indicator (transmitter is error passive).</summary>
    ErrorStateIndicator = 16,
    /// <summary>Error frame reported by the controller.</summary>
    Error = 32,
}

/// <summary>
/// One CAN or CAN FD frame. Immutable; <see cref="Data"/> is not copied, so do not mutate the buffer you pass in.
/// The text form is the <c>candump</c>/<c>cansend</c> notation: <c>123#DEADBEEF</c>, <c>18DAF110#0322F190</c>,
/// <c>123#R</c>, <c>123##1001122</c> (CAN FD, flags nibble then data).
/// </summary>
public readonly struct CanFrame : IEquatable<CanFrame>
{
    /// <summary>Largest standard (11-bit) identifier.</summary>
    public const uint MaxStandardId = 0x7FF;
    /// <summary>Largest extended (29-bit) identifier.</summary>
    public const uint MaxExtendedId = 0x1FFF_FFFF;

    /// <summary>Creates a frame and validates identifier and length.</summary>
    /// <param name="id">Identifier (11-bit unless <see cref="CanFrameFlags.Extended"/> is set).</param>
    /// <param name="data">Payload: 0–8 bytes classic, a valid CAN FD length (0–8, 12, 16, 20, 24, 32, 48, 64) for FD.</param>
    /// <param name="flags">Frame flags.</param>
    /// <param name="remoteLength">Requested length of a remote frame (DLC), classic only.</param>
    public CanFrame(uint id, ReadOnlyMemory<byte> data, CanFrameFlags flags = CanFrameFlags.None, int remoteLength = 0)
    {
        if ((flags & CanFrameFlags.Extended) != 0 ? id > MaxExtendedId : id > MaxStandardId)
            throw new ArgumentOutOfRangeException(nameof(id), $"Identifier 0x{id:X} does not fit a {((flags & CanFrameFlags.Extended) != 0 ? "29" : "11")}-bit frame.");
        if ((flags & CanFrameFlags.Fd) != 0)
        {
            if (!CanDlc.IsValidFdLength(data.Length)) throw new ArgumentException($"{data.Length} bytes is not a valid CAN FD length; use CanDlc.Pad.", nameof(data));
            if ((flags & CanFrameFlags.Remote) != 0) throw new ArgumentException("CAN FD has no remote frames.", nameof(flags));
        }
        else
        {
            if (data.Length > 8) throw new ArgumentException("Classic CAN frames carry at most 8 bytes (set CanFrameFlags.Fd).", nameof(data));
            if ((flags & (CanFrameFlags.BitRateSwitch | CanFrameFlags.ErrorStateIndicator)) != 0)
                throw new ArgumentException("BRS/ESI are CAN FD flags.", nameof(flags));
        }
        if (remoteLength is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(remoteLength));
        Id = id;
        Data = (flags & CanFrameFlags.Remote) != 0 ? ReadOnlyMemory<byte>.Empty : data;
        Flags = flags;
        RemoteLength = (byte)((flags & CanFrameFlags.Remote) != 0 ? remoteLength : 0);
        Timestamp = default;
    }

    private CanFrame(CanFrame other, DateTimeOffset timestamp)
    {
        Id = other.Id;
        Data = other.Data;
        Flags = other.Flags;
        RemoteLength = other.RemoteLength;
        Timestamp = timestamp;
    }

    /// <summary>Identifier without flag bits.</summary>
    public uint Id { get; }

    /// <summary>Payload.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Flags.</summary>
    public CanFrameFlags Flags { get; }

    /// <summary>Requested data length of a remote frame.</summary>
    public byte RemoteLength { get; }

    /// <summary>Receive time (default for frames created locally).</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>29-bit identifier.</summary>
    public bool IsExtended => (Flags & CanFrameFlags.Extended) != 0;

    /// <summary>CAN FD frame.</summary>
    public bool IsFd => (Flags & CanFrameFlags.Fd) != 0;

    /// <summary>Remote transmission request.</summary>
    public bool IsRemote => (Flags & CanFrameFlags.Remote) != 0;

    /// <summary>Error frame.</summary>
    public bool IsError => (Flags & CanFrameFlags.Error) != 0;

    /// <summary>Data length code (0–15).</summary>
    public int Dlc => IsRemote ? RemoteLength : CanDlc.FromLength(Data.Length);

    /// <summary>Returns a copy stamped with <paramref name="timestamp"/>.</summary>
    public CanFrame WithTimestamp(DateTimeOffset timestamp) => new(this, timestamp);

    /// <summary>Creates a classic or FD frame choosing the identifier width from the value.</summary>
    public static CanFrame Create(uint id, params byte[] data) =>
        new(id, data, (id > MaxStandardId ? CanFrameFlags.Extended : CanFrameFlags.None) | (data.Length > 8 ? CanFrameFlags.Fd : CanFrameFlags.None));

    /// <summary>Parses the candump/cansend notation.</summary>
    /// <exception cref="FormatException">The text is not a valid frame.</exception>
    public static CanFrame Parse(string text) =>
        TryParse(text, out var f) ? f : throw new FormatException($"'{text}' is not a CAN frame (expected e.g. 123#DEADBEEF).");

    /// <summary>Tries to parse the candump/cansend notation.</summary>
    public static bool TryParse(string? text, out CanFrame frame)
    {
        frame = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().Replace(".", "", StringComparison.Ordinal);
        var hash = s.IndexOf('#', StringComparison.Ordinal);
        if (hash is not (3 or 8)) return false;
        if (!uint.TryParse(s.AsSpan(0, hash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) return false;
        var flags = hash == 8 ? CanFrameFlags.Extended : CanFrameFlags.None;
        if ((flags & CanFrameFlags.Extended) != 0 ? id > MaxExtendedId : id > MaxStandardId) return false;
        var rest = s.AsSpan(hash + 1);
        try
        {
            if (rest.Length > 0 && (rest[0] == 'R' || rest[0] == 'r'))
            {
                var len = rest.Length > 1 ? HexDigit(rest[1]) : 0;
                if (len > 8) return false;
                frame = new CanFrame(id, ReadOnlyMemory<byte>.Empty, flags | CanFrameFlags.Remote, len);
                return true;
            }
            if (rest.Length > 0 && rest[0] == '#')
            {
                if (rest.Length < 2) return false;
                var fdFlags = HexDigit(rest[1]);
                if (fdFlags < 0) return false;
                flags |= CanFrameFlags.Fd;
                if ((fdFlags & 1) != 0) flags |= CanFrameFlags.BitRateSwitch;
                if ((fdFlags & 2) != 0) flags |= CanFrameFlags.ErrorStateIndicator;
                rest = rest[2..];
            }
            if (rest.Length % 2 != 0) return false;
            var data = Convert.FromHexString(rest);
            if ((flags & CanFrameFlags.Fd) == 0 && data.Length > 8) return false;
            if ((flags & CanFrameFlags.Fd) != 0 && !CanDlc.IsValidFdLength(data.Length)) return false;
            frame = new CanFrame(id, data, flags);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>The candump/cansend notation.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(IsExtended ? 9 + Data.Length * 2 : 4 + Data.Length * 2);
        sb.Append(IsExtended ? Id.ToString("X8", CultureInfo.InvariantCulture) : Id.ToString("X3", CultureInfo.InvariantCulture)).Append('#');
        if (IsRemote) return sb.Append('R').Append(RemoteLength > 0 ? RemoteLength.ToString(CultureInfo.InvariantCulture) : "").ToString();
        if (IsFd)
        {
            var f = ((Flags & CanFrameFlags.BitRateSwitch) != 0 ? 1 : 0) | ((Flags & CanFrameFlags.ErrorStateIndicator) != 0 ? 2 : 0);
            sb.Append('#').Append(f.ToString("X", CultureInfo.InvariantCulture));
        }
        return sb.Append(Convert.ToHexString(Data.Span)).ToString();
    }

    /// <summary>Equal identifier, flags and payload bytes (timestamps are ignored).</summary>
    public bool Equals(CanFrame other) =>
        Id == other.Id && Flags == other.Flags && RemoteLength == other.RemoteLength && Data.Span.SequenceEqual(other.Data.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CanFrame f && Equals(f);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Flags, Data.Length);

    /// <summary>Equality.</summary>
    public static bool operator ==(CanFrame left, CanFrame right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(CanFrame left, CanFrame right) => !left.Equals(right);
}

/// <summary>DLC ↔ length conversions for classic CAN and CAN FD.</summary>
public static class CanDlc
{
    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    /// <summary>Length for a DLC (0–15). Classic DLC 9–15 also mean 8 bytes.</summary>
    public static int ToLength(int dlc, bool fd = true) => dlc is < 0 or > 15
        ? throw new ArgumentOutOfRangeException(nameof(dlc))
        : fd ? FdLengths[dlc] : Math.Min(dlc, 8);

    /// <summary>Smallest DLC whose length is at least <paramref name="length"/>.</summary>
    public static int FromLength(int length)
    {
        for (var i = 0; i < FdLengths.Length; i++)
            if (FdLengths[i] >= length) return i;
        throw new ArgumentOutOfRangeException(nameof(length), "CAN FD frames carry at most 64 bytes.");
    }

    /// <summary>True for 0–8, 12, 16, 20, 24, 32, 48 and 64.</summary>
    public static bool IsValidFdLength(int length) => Array.IndexOf(FdLengths, length) >= 0;

    /// <summary>Pads <paramref name="data"/> up to the next valid CAN FD length.</summary>
    public static byte[] Pad(ReadOnlySpan<byte> data, byte padding = 0xCC)
    {
        var buf = new byte[ToLength(FromLength(data.Length))];
        data.CopyTo(buf);
        buf.AsSpan(data.Length).Fill(padding);
        return buf;
    }
}

/// <summary>
/// An acceptance filter: a frame passes when <c>(frame.Id &amp; Mask) == (Id &amp; Mask)</c> and, if set, the identifier
/// width matches. <see cref="All"/> accepts everything.
/// </summary>
/// <param name="Id">Identifier to compare.</param>
/// <param name="Mask">Bits that must match.</param>
/// <param name="Extended">Required identifier width, or null for both.</param>
public readonly record struct CanFilter(uint Id, uint Mask, bool? Extended = null)
{
    /// <summary>Accepts every frame.</summary>
    public static CanFilter All => new(0, 0);

    /// <summary>Accepts exactly one identifier; the width is 29-bit when the value needs it, otherwise 11-bit unless given.</summary>
    public static CanFilter Exact(uint id, bool? extended = null) => new(id, CanFrame.MaxExtendedId, extended ?? id > CanFrame.MaxStandardId);

    /// <summary>True when <paramref name="frame"/> passes the filter.</summary>
    public bool Matches(in CanFrame frame) =>
        (frame.Id & Mask) == (Id & Mask) && (Extended is null || Extended == frame.IsExtended);
}
