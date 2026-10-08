using System.Globalization;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>Frame-lane description of DLMS traffic: HDLC frames, wrapper PDUs and the APDUs inside them.</summary>
public static class DlmsAnatomy
{
    /// <summary>Describes an HDLC frame (starting with 7E), a wrapper PDU (00 01 …) or a bare APDU.</summary>
    public static IReadOnlyList<FrameField> Describe(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return [];
        if (data[0] == HdlcFrame.Flag) return Hdlc(data);
        if (data.Length >= 8 && data[0] == 0 && data[1] == 1) return Wrapper(data);
        var fields = new List<FrameField>();
        Apdu(data, 0, fields);
        return fields;
    }

    private static List<FrameField> Hdlc(ReadOnlySpan<byte> data)
    {
        var status = HdlcFrame.TryRead(data, out var frame, out _, out var error);
        if (status != HdlcFrame.ReadStatus.Frame) return [new FrameField("Invalid", 0, data.Length, FrameFieldKind.Error, error ?? "incomplete HDLC frame")];
        var fields = new List<FrameField>
        {
            new("Flag", 0, 1, FrameFieldKind.Delimiter),
            new("Format", 1, 2, FrameFieldKind.Length, $"{((data[1] & 7) << 8) | data[2]} B{(frame!.Segmented ? ", segmented" : "")}"),
        };
        var pos = 3;
        var destSize = frame.Destination.EncodedSize;
        fields.Add(new FrameField("Dest", pos, destSize, FrameFieldKind.Address, frame.Destination.ToString()));
        pos += destSize;
        var srcSize = frame.Source.EncodedSize;
        fields.Add(new FrameField("Src", pos, srcSize, FrameFieldKind.Address, frame.Source.ToString()));
        pos += srcSize;
        fields.Add(new FrameField("Control", pos++, 1, FrameFieldKind.Function, HdlcControl.Describe(frame.Control)));
        if (frame.Information.Length > 0)
        {
            fields.Add(new FrameField("HCS", pos, 2, FrameFieldKind.Checksum));
            pos += 2;
            var info = frame.Information;
            if (info.Length >= 3 && info[0] == 0xE6)
            {
                fields.Add(new FrameField("LLC", pos, 3, FrameFieldKind.Header, info[1] == 0xE6 ? "request" : "response"));
                Apdu(info.AsSpan(3), pos + 3, fields, partial: frame.Segmented);
            }
            else
            {
                fields.Add(new FrameField("Info", pos, info.Length, FrameFieldKind.Data, info.Length >= 2 && info[0] == 0x81 ? "HDLC parameters" : "segment"));
            }

            pos += info.Length;
        }

        fields.Add(new FrameField("FCS", pos, 2, FrameFieldKind.Checksum));
        fields.Add(new FrameField("Flag", pos + 2, 1, FrameFieldKind.Delimiter));
        return fields;
    }

    private static List<FrameField> Wrapper(ReadOnlySpan<byte> data)
    {
        var fields = new List<FrameField>
        {
            new("Version", 0, 2, FrameFieldKind.Header, "1"),
            new("Source", 2, 2, FrameFieldKind.Address, ((data[2] << 8) | data[3]).ToString(CultureInfo.InvariantCulture)),
            new("Dest", 4, 2, FrameFieldKind.Address, ((data[4] << 8) | data[5]).ToString(CultureInfo.InvariantCulture)),
            new("Length", 6, 2, FrameFieldKind.Length, ((data[6] << 8) | data[7]).ToString(CultureInfo.InvariantCulture)),
        };
        Apdu(data[8..], 8, fields);
        return fields;
    }

    private static void Apdu(ReadOnlySpan<byte> apdu, int at, List<FrameField> fields, bool partial = false)
    {
        if (apdu.IsEmpty) return;
        if (partial || !DlmsApdu.TryDecode(apdu, out var pdu, out _))
        {
            fields.Add(new FrameField("APDU", at, apdu.Length, FrameFieldKind.Data, partial ? "first segment" : "undecoded"));
            return;
        }

        var name = DlmsApdu.Describe(pdu!);
        switch (pdu)
        {
            case GetRequestPdu or SetRequestPdu or ActionRequestPdu when apdu.Length >= 12:
                fields.Add(new FrameField("Service", at, 2, FrameFieldKind.Function, name.Split(' ')[0]));
                fields.Add(new FrameField("Invoke", at + 2, 1, FrameFieldKind.Header));
                var reference = pdu switch { GetRequestPdu g => g.Attribute, SetRequestPdu s => s.Attribute, ActionRequestPdu a => a.Method, _ => default };
                fields.Add(new FrameField("Class", at + 3, 2, FrameFieldKind.Header, CosemClass.Name(reference.ClassId)));
                fields.Add(new FrameField("OBIS", at + 5, 6, FrameFieldKind.Address, reference.LogicalName.ToString()));
                fields.Add(new FrameField(pdu is ActionRequestPdu ? "Method" : "Attr", at + 11, 1, FrameFieldKind.Length, reference.Index.ToString(CultureInfo.InvariantCulture)));
                if (apdu.Length > 12) fields.Add(new FrameField("Params", at + 12, apdu.Length - 12, FrameFieldKind.Data));
                break;
            case GetResponsePdu r when apdu.Length >= 4:
                fields.Add(new FrameField("Service", at, 2, FrameFieldKind.Function, r.IsBlock ? "GET.response block" : "GET.response"));
                fields.Add(new FrameField("Invoke", at + 2, 1, FrameFieldKind.Header));
                var ok = r.Data is not null || r.Block is not null;
                fields.Add(new FrameField(ok ? "Data" : "Result", at + 3, apdu.Length - 3, ok ? FrameFieldKind.Data : FrameFieldKind.Error, ok ? name["GET.response ".Length..] : r.Result.ToString()));
                break;
            default:
                var error = pdu is ErrorPdu || pdu is AarePdu { Accepted: false };
                fields.Add(new FrameField("Tag", at, 1, error ? FrameFieldKind.Error : FrameFieldKind.Function, name));
                if (apdu.Length > 1) fields.Add(new FrameField("Body", at + 1, apdu.Length - 1, FrameFieldKind.Data));
                break;
        }
    }
}
