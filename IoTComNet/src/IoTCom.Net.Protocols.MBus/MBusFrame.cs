using System.Globalization;

namespace IoTCom.Net.Protocols.MBus;

/// <summary>M-Bus frame formats (EN 13757-2).</summary>
public enum MBusFrameType
{
    /// <summary>Single character acknowledgement (0xE5).</summary>
    Ack,
    /// <summary>Short frame: 10 C A CS 16.</summary>
    Short,
    /// <summary>Control frame: long frame without user data (L = 3).</summary>
    Control,
    /// <summary>Long frame: 68 L L 68 C A CI data CS 16.</summary>
    Long,
}

/// <summary>Control field values and addresses.</summary>
public static class MBusControl
{
    /// <summary>SND_NKE: link reset (initialisation), answered by E5.</summary>
    public const byte SndNke = 0x40;
    /// <summary>SND_UD: send user data (FCB clear).</summary>
    public const byte SndUd = 0x53;
    /// <summary>REQ_UD2: request class 2 data (FCB clear; 0x7B with FCB set).</summary>
    public const byte ReqUd2 = 0x5B;
    /// <summary>REQ_UD1: request class 1 (alarm) data.</summary>
    public const byte ReqUd1 = 0x5A;
    /// <summary>RSP_UD: response with user data (ACD/DFC bits may be set).</summary>
    public const byte RspUd = 0x08;
    /// <summary>The frame count bit of REQ_UD2/SND_UD.</summary>
    public const byte Fcb = 0x20;

    /// <summary>Unconfigured slave.</summary>
    public const byte AddressUnconfigured = 0;
    /// <summary>Secondary addressing (the selected slave answers).</summary>
    public const byte AddressNetworkLayer = 253;
    /// <summary>Broadcast, slaves reply (only one slave on the bus).</summary>
    public const byte AddressBroadcastReply = 254;
    /// <summary>Broadcast, no reply.</summary>
    public const byte AddressBroadcast = 255;

    /// <summary>Name of a control byte.</summary>
    public static string Name(byte c) => (c & 0xDF) switch
    {
        0x40 => "SND_NKE",
        0x53 => "SND_UD",
        0x5B => "REQ_UD2",
        0x5A => "REQ_UD1",
        _ when (c & 0xCF) == 0x08 => "RSP_UD",
        _ => $"C=0x{c:X2}",
    };
}

/// <summary>CI (control information) values.</summary>
public static class MBusCi
{
    /// <summary>Data send (master → slave).</summary>
    public const byte DataSend = 0x51;
    /// <summary>Selection of a slave by secondary address.</summary>
    public const byte SelectSlave = 0x52;
    /// <summary>Response, variable data structure with the 12-byte long header.</summary>
    public const byte VariableLong = 0x72;
    /// <summary>Response, variable data structure with the 4-byte short header.</summary>
    public const byte VariableShort = 0x7A;
    /// <summary>Response, variable data structure without header.</summary>
    public const byte VariableNone = 0x78;
}

/// <summary>An M-Bus frame: control, address, CI and user data.</summary>
/// <param name="Type">Format.</param>
/// <param name="Control">C field.</param>
/// <param name="Address">A field (primary address).</param>
/// <param name="Ci">CI field (control and long frames).</param>
/// <param name="Data">User data after CI.</param>
public sealed record MBusFrame(MBusFrameType Type, byte Control = 0, byte Address = 0, byte Ci = 0, byte[]? Data = null)
{
    /// <summary>The E5 acknowledgement.</summary>
    public static MBusFrame Ack { get; } = new(MBusFrameType.Ack);

    /// <summary>A short frame.</summary>
    public static MBusFrame Short(byte control, byte address) => new(MBusFrameType.Short, control, address);

    /// <summary>A long frame.</summary>
    public static MBusFrame Long(byte control, byte address, byte ci, byte[] data) => new(MBusFrameType.Long, control, address, ci, data);

    /// <summary>User data.</summary>
    public byte[] UserData => Data ?? [];

    /// <summary>Encodes the frame.</summary>
    public byte[] Encode()
    {
        switch (Type)
        {
            case MBusFrameType.Ack:
                return [0xE5];
            case MBusFrameType.Short:
                return [0x10, Control, Address, (byte)(Control + Address), 0x16];
            default:
                var data = UserData;
                var length = 3 + data.Length;
                if (length > 255) throw new InvalidOperationException("M-Bus user data exceeds 252 bytes.");
                var frame = new byte[6 + length];
                frame[0] = 0x68;
                frame[1] = frame[2] = (byte)length;
                frame[3] = 0x68;
                frame[4] = Control;
                frame[5] = Address;
                frame[6] = Ci;
                data.CopyTo(frame, 7);
                byte cs = 0;
                for (var i = 4; i < 4 + length; i++) cs += frame[i];
                frame[4 + length] = cs;
                frame[5 + length] = 0x16;
                return frame;
        }
    }

    /// <summary>Outcome of <see cref="TryRead"/>.</summary>
    public enum ReadStatus
    {
        /// <summary>A frame.</summary>
        Frame,
        /// <summary>More bytes needed.</summary>
        NeedMore,
        /// <summary>Bytes skipped (noise or a bad frame).</summary>
        Skipped,
    }

    /// <summary>Reads the next frame; <paramref name="consumed"/> bytes may be discarded.</summary>
    public static ReadStatus TryRead(ReadOnlySpan<byte> data, out MBusFrame? frame, out int consumed, out string? error)
    {
        (frame, consumed, error) = (null, 0, null);
        if (data.IsEmpty) return ReadStatus.NeedMore;
        switch (data[0])
        {
            case 0xE5:
                (frame, consumed) = (Ack, 1);
                return ReadStatus.Frame;
            case 0x10:
                if (data.Length < 5) return ReadStatus.NeedMore;
                consumed = 5;
                if (data[4] != 0x16 || (byte)(data[1] + data[2]) != data[3])
                {
                    (consumed, error) = (1, "Short frame with a bad checksum or stop byte.");
                    return ReadStatus.Skipped;
                }

                frame = Short(data[1], data[2]);
                return ReadStatus.Frame;
            case 0x68:
                if (data.Length < 4) return ReadStatus.NeedMore;
                if (data[1] != data[2] || data[3] != 0x68 || data[1] < 3)
                {
                    (consumed, error) = (1, "Long frame header is inconsistent.");
                    return ReadStatus.Skipped;
                }

                var length = data[1];
                if (data.Length < 6 + length) return ReadStatus.NeedMore;
                byte cs = 0;
                for (var i = 4; i < 4 + length; i++) cs += data[i];
                if (data[4 + length] != cs || data[5 + length] != 0x16)
                {
                    (consumed, error) = (1, $"Long frame checksum 0x{data[4 + length]:X2}, expected 0x{cs:X2}.");
                    return ReadStatus.Skipped;
                }

                consumed = 6 + length;
                frame = new MBusFrame(length == 3 ? MBusFrameType.Control : MBusFrameType.Long, data[4], data[5], data[6], data.Slice(7, length - 3).ToArray());
                return ReadStatus.Frame;
            default:
                (consumed, error) = (1, $"Unexpected byte 0x{data[0]:X2}.");
                return ReadStatus.Skipped;
        }
    }

    /// <summary>Decodes exactly one frame or throws <see cref="FormatException"/>.</summary>
    public static MBusFrame Decode(ReadOnlySpan<byte> data) =>
        TryRead(data, out var f, out var n, out var e) == ReadStatus.Frame && n == data.Length ? f! : throw new FormatException(e ?? "Incomplete or trailing bytes in the M-Bus frame.");

    /// <inheritdoc />
    public override string ToString() => Type switch
    {
        MBusFrameType.Ack => "ACK (E5)",
        MBusFrameType.Short => $"{MBusControl.Name(Control)} → {Address}",
        _ => string.Create(CultureInfo.InvariantCulture, $"{MBusControl.Name(Control)} {Address} CI 0x{Ci:X2} {UserData.Length} B"),
    };
}
