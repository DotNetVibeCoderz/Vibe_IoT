using System.Buffers.Binary;
using System.Globalization;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>Splits Modbus frames into named fields for inspectors (CLI <c>modbus decode</c>, Gallery, dashboard).</summary>
public static class ModbusAnatomy
{
    /// <summary>Describes every field of a complete ADU.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> frame, ModbusFramingMode mode, bool isRequest)
    {
        var fields = new List<FrameField>();
        switch (mode)
        {
            case ModbusFramingMode.Tcp:
                if (frame.Length < 8) return [new FrameField("Raw", 0, frame.Length, FrameFieldKind.Data)];
                fields.Add(new("Transaction", 0, 2, FrameFieldKind.Header, BinaryPrimitives.ReadUInt16BigEndian(frame).ToString(CultureInfo.InvariantCulture)));
                fields.Add(new("Protocol", 2, 2, FrameFieldKind.Header, "0"));
                fields.Add(new("Length", 4, 2, FrameFieldKind.Length, BinaryPrimitives.ReadUInt16BigEndian(frame[4..]).ToString(CultureInfo.InvariantCulture)));
                fields.Add(new("Unit", 6, 1, FrameFieldKind.Address, frame[6].ToString(CultureInfo.InvariantCulture)));
                AddPdu(fields, frame[7..], 7, 1, isRequest);
                break;
            case ModbusFramingMode.Rtu:
                if (frame.Length < 4) return [new FrameField("Raw", 0, frame.Length, FrameFieldKind.Data)];
                fields.Add(new("Unit", 0, 1, FrameFieldKind.Address, frame[0].ToString(CultureInfo.InvariantCulture)));
                AddPdu(fields, frame[1..^2], 1, 1, isRequest);
                fields.Add(new("CRC", frame.Length - 2, 2, FrameFieldKind.Checksum, $"0x{frame[^1]:X2}{frame[^2]:X2}"));
                break;
            case ModbusFramingMode.Ascii:
                if (!ModbusFraming.Ascii.TryDecode(frame, isRequest, out var adu)) return [new FrameField("Raw", 0, frame.Length, FrameFieldKind.Data)];
                fields.Add(new("Start", 0, 1, FrameFieldKind.Delimiter, ":"));
                fields.Add(new("Unit", 1, 2, FrameFieldKind.Address, adu.UnitId.ToString(CultureInfo.InvariantCulture)));
                AddPdu(fields, adu.Pdu, 3, 2, isRequest);
                var lrcAt = 3 + adu.Pdu.Length * 2;
                fields.Add(new("LRC", lrcAt, 2, FrameFieldKind.Checksum));
                fields.Add(new("CR LF", lrcAt + 2, frame.Length - lrcAt - 2, FrameFieldKind.Delimiter));
                break;
        }
        return fields;
    }

    private static void AddPdu(List<FrameField> fields, ReadOnlySpan<byte> pdu, int offset, int scale, bool isRequest)
    {
        if (pdu.IsEmpty) return;
        var fc = pdu[0];
        var pduLength = pdu.Length;
        void Add(string name, int at, int len, FrameFieldKind kind, string? value = null)
        {
            if (at + len > pduLength) len = Math.Max(0, pduLength - at);
            if (len > 0) fields.Add(new FrameField(name, offset + at * scale, len * scale, kind, value));
        }
        static ushort U16(ReadOnlySpan<byte> p, int at) => p.Length >= at + 2 ? BinaryPrimitives.ReadUInt16BigEndian(p[at..]) : (ushort)0;
        static string S(int v) => v.ToString(CultureInfo.InvariantCulture);

        if ((fc & 0x80) != 0)
        {
            Add("Function", 0, 1, FrameFieldKind.Error, $"{(ModbusFunctionCode)(fc & 0x7F)} (exception)");
            Add("Exception", 1, 1, FrameFieldKind.Error, pdu.Length > 1 ? ((ModbusExceptionCode)pdu[1]).ToString() : null);
            return;
        }
        Add("Function", 0, 1, FrameFieldKind.Function, Enum.IsDefined((ModbusFunctionCode)fc) ? ((ModbusFunctionCode)fc).ToString() : $"0x{fc:X2}");
        switch (fc)
        {
            case 0x01 or 0x02 or 0x03 or 0x04 when isRequest:
                Add("Address", 1, 2, FrameFieldKind.Data, S(U16(pdu, 1)));
                Add("Quantity", 3, 2, FrameFieldKind.Length, S(U16(pdu, 3)));
                break;
            case 0x01 or 0x02 or 0x03 or 0x04 or 0x17 when !isRequest:
                Add("Byte count", 1, 1, FrameFieldKind.Length, pdu.Length > 1 ? S(pdu[1]) : null);
                Add("Values", 2, pdu.Length - 2, FrameFieldKind.Data);
                break;
            case 0x05 or 0x06:
                Add("Address", 1, 2, FrameFieldKind.Data, S(U16(pdu, 1)));
                Add("Value", 3, 2, FrameFieldKind.Data, fc == 0x05 ? (U16(pdu, 3) == 0xFF00 ? "ON" : "OFF") : S(U16(pdu, 3)));
                break;
            case 0x0F or 0x10:
                Add("Address", 1, 2, FrameFieldKind.Data, S(U16(pdu, 1)));
                Add("Quantity", 3, 2, FrameFieldKind.Length, S(U16(pdu, 3)));
                if (isRequest)
                {
                    Add("Byte count", 5, 1, FrameFieldKind.Length, pdu.Length > 5 ? S(pdu[5]) : null);
                    Add("Values", 6, pdu.Length - 6, FrameFieldKind.Data);
                }
                break;
            case 0x17 when isRequest:
                Add("Read address", 1, 2, FrameFieldKind.Data, S(U16(pdu, 1)));
                Add("Read quantity", 3, 2, FrameFieldKind.Length, S(U16(pdu, 3)));
                Add("Write address", 5, 2, FrameFieldKind.Data, S(U16(pdu, 5)));
                Add("Write quantity", 7, 2, FrameFieldKind.Length, S(U16(pdu, 7)));
                Add("Byte count", 9, 1, FrameFieldKind.Length);
                Add("Values", 10, pdu.Length - 10, FrameFieldKind.Data);
                break;
            default:
                Add("Data", 1, pdu.Length - 1, FrameFieldKind.Data);
                break;
        }
    }
}
