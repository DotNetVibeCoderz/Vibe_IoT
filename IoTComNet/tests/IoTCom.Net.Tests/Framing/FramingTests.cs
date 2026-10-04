using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using IoTCom.Net.Framing;

namespace IoTCom.Net.Tests.Framing;

public class CrcTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("crc.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Matches_conformance_vector(string json)
    {
        var v = Conformance.Parse(json);
        var algo = CrcCatalog.Find(v.GetProperty("algorithm").GetString()!);
        Assert.NotNull(algo);
        var expected = ulong.Parse(v.GetProperty("expected").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Assert.Equal(expected, algo!.Compute(v.Hex("input")));
    }

    [Fact]
    public void Every_preset_passes_its_check_value()
    {
        Assert.All(CrcCatalog.All, a => Assert.True(a.SelfTest(), a.Parameters.Name));
    }

    [Fact]
    public void Incremental_equals_one_shot()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        foreach (var a in CrcCatalog.All)
        {
            var reg = a.Start();
            reg = a.Update(reg, data.AsSpan(0, 333));
            reg = a.Update(reg, data.AsSpan(333));
            Assert.Equal(a.Compute(data), a.Finish(reg));
        }
    }

    [Fact]
    public void Specialised_modbus_crc_matches_catalog()
    {
        var data = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        Assert.Equal(CrcCatalog.Crc16Modbus.Compute(data), Crc16.Modbus(data));
    }

    [Theory]
    [InlineData("modbus", "CRC-16/MODBUS")]
    [InlineData("x25", "CRC-16/IBM-SDLC")]
    [InlineData("mavlink", "CRC-16/MCRF4XX")]
    [InlineData("crc32c", "CRC-32/ISCSI")]
    [InlineData("CRC-16/XMODEM", "CRC-16/XMODEM")]
    public void Find_resolves_aliases(string alias, string expected) => Assert.Equal(expected, CrcCatalog.Find(alias)!.Parameters.Name);

    [Fact]
    public void Lrc_matches_modbus_ascii_spec_example() => Assert.Equal(0x7E, Lrc.Compute([0x11, 0x03, 0x00, 0x6B, 0x00, 0x03]));
}

public class CobsTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("cobs.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Encode_and_decode_conformance(string json)
    {
        var v = Conformance.Parse(json);
        var decoded = v.Hex("decoded");
        var encoded = v.Hex("encoded");
        var buf = new byte[Cobs.MaxEncodedLength(decoded.Length)];
        var n = Cobs.Encode(decoded, buf);
        Assert.Equal(encoded, buf[..n]);
        var back = new byte[encoded.Length];
        var m = Cobs.Decode(encoded, back);
        Assert.Equal(decoded, back[..m]);
    }

    [Fact]
    public void Streaming_decoder_handles_fragmented_input()
    {
        var cobs = new Cobs();
        var frames = new[] { new byte[] { 1, 0, 2 }, new byte[] { 0, 0, 0 }, Enumerable.Range(0, 600).Select(i => (byte)i).ToArray() };
        var wire = new ArrayBufferWriter<byte>();
        foreach (var f in frames) cobs.Encode(f, wire);
        var decoded = DecodeFragmented(cobs, wire.WrittenSpan.ToArray(), fragment: 3);
        Assert.Equal(frames.Length, decoded.Count);
        for (var i = 0; i < frames.Length; i++) Assert.Equal(frames[i], decoded[i]);
    }

    [Fact]
    public void Decode_rejects_embedded_zero() => Assert.Equal(-1, Cobs.Decode([0x03, 0x11, 0x00], new byte[8]));

    internal static List<byte[]> DecodeFragmented(IFrameDecoder decoder, byte[] wire, int fragment)
    {
        // Simulate a pipe that receives data a few bytes at a time.
        var result = new List<byte[]>();
        var pending = Array.Empty<byte>();
        var payload = new ArrayBufferWriter<byte>();
        for (var i = 0; i < wire.Length; i += fragment)
        {
            pending = [.. pending, .. wire.AsSpan(i, Math.Min(fragment, wire.Length - i))];
            var seq = new ReadOnlySequence<byte>(pending);
            while (true)
            {
                payload.ResetWrittenCount();
                var status = decoder.TryDecode(ref seq, payload);
                if (status == FrameDecodeStatus.NeedMoreData) break;
                if (status == FrameDecodeStatus.Frame) result.Add(payload.WrittenSpan.ToArray());
            }
            pending = seq.ToArray();
        }
        return result;
    }
}

public class SlipTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("slip.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Encode_conformance(string json)
    {
        var v = Conformance.Parse(json);
        var decoded = v.Hex("decoded");
        var buf = new byte[Slip.MaxEncodedLength(decoded.Length)];
        var n = Slip.Encode(decoded, buf);
        Assert.Equal(v.Hex("encoded"), buf[..n]);
    }

    [Fact]
    public void Streaming_roundtrip_with_special_bytes()
    {
        var slip = new Slip();
        var frames = new[] { new byte[] { 0xC0, 0xDB, 0xDC, 0xDD }, new byte[] { 1, 2, 3 }, new byte[] { 0xDB } };
        var wire = new ArrayBufferWriter<byte>();
        foreach (var f in frames) slip.Encode(f, wire);
        var decoded = CobsTests.DecodeFragmented(slip, wire.WrittenSpan.ToArray(), fragment: 2);
        Assert.Equal(frames, decoded);
    }

    [Fact]
    public void Invalid_escape_is_dropped_and_stream_recovers()
    {
        var wire = new byte[] { 0xC0, 0x01, 0xDB, 0x55, 0xC0, 0x07, 0x08, 0xC0 };
        var decoded = CobsTests.DecodeFragmented(new Slip(), wire, fragment: 8);
        Assert.Single(decoded);
        Assert.Equal(new byte[] { 7, 8 }, decoded[0]);
    }
}

public class HdlcTests
{
    [Fact]
    public void Roundtrip_with_fcs_and_escaping()
    {
        var hdlc = new Hdlc();
        var frames = new[] { new byte[] { 0xFF, 0x03, 0x7E, 0x7D, 0x01, 0x42 }, new byte[] { 0x10, 0x20, 0x30 } };
        var wire = new ArrayBufferWriter<byte>();
        foreach (var f in frames) hdlc.Encode(f, wire);
        Assert.Equal(4, wire.WrittenSpan.ToArray().Count(b => b == Hdlc.Flag)); // 0x7E in the payload must be escaped
        var decoded = CobsTests.DecodeFragmented(hdlc, wire.WrittenSpan.ToArray(), fragment: 5);
        Assert.Equal(frames, decoded);
    }

    [Fact]
    public void Corrupted_fcs_is_rejected()
    {
        var hdlc = new Hdlc();
        var wire = new ArrayBufferWriter<byte>();
        hdlc.Encode([1, 2, 3, 4], wire);
        var bytes = wire.WrittenSpan.ToArray();
        bytes[2] ^= 0x01;
        Assert.Empty(CobsTests.DecodeFragmented(hdlc, bytes, 64));
    }

    [Fact]
    public async Task Pipe_extensions_read_frames()
    {
        var pipe = new Pipe();
        var lines = new LineFraming();
        await pipe.Writer.WriteFrameAsync(lines, "$GPGGA,1"u8.ToArray());
        await pipe.Writer.WriteFrameAsync(lines, "$GPRMC,2"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        var got = new List<string>();
        await foreach (var f in pipe.Reader.ReadFramesAsync(lines)) got.Add(System.Text.Encoding.ASCII.GetString(f));
        Assert.Equal(["$GPGGA,1", "$GPRMC,2"], got);
    }
}
