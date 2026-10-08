using Google.Protobuf.WellKnownTypes;
using IoTCom.Net.Serialization.MessagePack;
using IoTCom.Net.Serialization.Protobuf;
using IoTCom.Net.Serialization.SenML;
using IoTCom.Net.Serialization.Tlv;
using MessagePack;

namespace IoTCom.Net.Tests.Protocols;

public class SerializationTests
{
    [Fact]
    public void Protobuf_codec_round_trips_and_delimits()
    {
        var codec = new ProtobufCodec<Timestamp>();
        var ts = new Timestamp { Seconds = 1_760_000_000, Nanos = 5 };
        Assert.Equal("application/x-protobuf", codec.ContentType);
        Assert.Equal("0880F09DC7061005", Convert.ToHexString(codec.Encode(ts)));
        Assert.Equal(ts, codec.Decode(codec.Encode(ts)));
        var stream = ProtobufCodec<Timestamp>.EncodeDelimited([ts, new Timestamp { Seconds = 1 }]);
        Assert.Equal([ts, new Timestamp { Seconds = 1 }], ProtobufCodec<Timestamp>.DecodeDelimited(stream));
        Assert.Throws<ProtocolException>(() => codec.Decode([0x08, 0xFF]));
    }

    [Fact]
    public void Protobuf_wire_inspects_without_schema()
    {
        // field 1 varint 150, field 2 "hi", field 3 nested { field 1 varint 1 }
        byte[] data = [0x08, 0x96, 0x01, 0x12, 0x02, 0x68, 0x69, 0x1A, 0x02, 0x08, 0x01];
        Assert.True(ProtobufWire.TryInspect(data, out var fields));
        Assert.Equal([1, 2, 3], fields.Select(f => f.Number));
        Assert.Equal("150", fields[0].Value);
        Assert.Equal("\"hi\"", fields[1].Value);
        Assert.Single(fields[2].Nested);
        Assert.False(ProtobufWire.TryInspect([0x12, 0x09], out _));
        Assert.Equal(FrameFieldKind.Error, ProtobufWire.Describe([0x07]).Single().Kind);
    }

    [Fact]
    public void MessagePack_codec_and_json_view()
    {
        var codec = new MessagePackCodec<Dictionary<string, double>>(MessagePackSerializerOptions.Standard);
        var bytes = codec.Encode(new() { ["t"] = 21.5 });
        Assert.Equal("81A174CB4035800000000000", Convert.ToHexString(bytes));
        Assert.Equal(21.5, codec.Decode(bytes)["t"]);
        Assert.Equal("{\"t\":21.5}", MessagePackView.ToJson(bytes));
        Assert.Null(MessagePackView.ToJson(new byte[] { 0xC1 }));
        Assert.Throws<ProtocolException>(() => codec.Decode([0xC1]));
    }

    [Fact]
    public void Simple_tlv_round_trips_and_rejects_overruns()
    {
        var items = new[] { TlvItem.Primitive(0x01, [0x2A]), TlvItem.Primitive(0x02, "OK"u8) };
        var bytes = TlvFormat.Simple.Encode(items);
        Assert.Equal("01012A02024F4B", Convert.ToHexString(bytes));
        var back = TlvFormat.Simple.Decode(bytes);
        Assert.Equal(2, back.Count);
        Assert.Equal("OK"u8.ToArray(), back[1].Value);
        Assert.Equal("00010100FF", Convert.ToHexString(new TlvFormat(2, 2, BigEndian: false).Encode([TlvItem.Primitive(0x100, [0xFF])])));
        Assert.Throws<FormatException>(() => TlvFormat.Simple.Decode([0x01, 0x05, 0x00]));
    }

    [Fact]
    public void Ber_tlv_decodes_emv_style_nesting()
    {
        // 6F (FCI) { 84 (DF name) A0000000031010, A5 { 50 "VISA" } }
        var data = Convert.FromHexString("6F148407A0000000031010A5095004564953419F3800");
        var top = Assert.Single(BerTlv.Decode(data));
        Assert.Equal(0x6Fu, top.Tag);
        Assert.Equal("A0000000031010", Convert.ToHexString(top.Find(0x84)!.Value));
        Assert.Equal("VISA"u8.ToArray(), top.Find(0x50)!.Value);
        Assert.NotNull(top.Find(0x9F38));
        Assert.True(BerTlv.IsConstructed(0xA5));
        Assert.False(BerTlv.IsConstructed(0x9F38));
        Assert.Equal(data, BerTlv.Encode([top]));
        Assert.Throws<FormatException>(() => BerTlv.Decode([0x84, 0x05, 0x01]));
        Assert.Contains(BerTlv.Describe(data), f => f.Name == "50");
    }

    [Fact]
    public void Ber_tlv_long_lengths()
    {
        var big = TlvItem.Primitive(0x04, new byte[300]);
        var bytes = BerTlv.Encode([big]);
        Assert.Equal("0482012C", Convert.ToHexString(bytes.AsSpan(0, 4)));
        Assert.Equal(300, BerTlv.Decode(bytes).Single().Value.Length);
    }

    [Fact]
    public void SenML_payload_codec_in_both_encodings()
    {
        IReadOnlyList<SenMLRecord> pack = [new SenMLRecord { BaseName = "urn:dev:1:", Name = "temp", Unit = "Cel", Value = 27.5 }];
        foreach (var cbor in new[] { false, true })
        {
            var codec = new SenMLPayloadCodec(cbor);
            var back = codec.Decode(codec.Encode(pack));
            Assert.Equal(27.5, back.Single().Value);
            Assert.Equal(cbor ? "application/senml+cbor" : "application/senml+json", codec.ContentType);
        }
    }
}
