using System.Text;
using IoTCom.Net.Protocols.Nfc;

namespace IoTCom.Net.Tests.Protocols;

public class NdefCodecTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    [Fact]
    public void Uri_and_text_records_match_the_classic_bytes()
    {
        // The NFC Forum example: https://www.nxp.com with prefix code 0x02.
        var uri = new NdefMessage([NdefRecord.Uri("https://www.nxp.com")]);
        Assert.Equal(H("D1 01 08 55 02 6E78702E636F6D"), uri.Encode());
        Assert.True(NdefMessage.Parse(uri.Encode()).Records[0].TryGetUri(out var u));
        Assert.Equal("https://www.nxp.com", u);
        Assert.Equal(0x05, NdefRecord.Uri("tel:+6221555").Payload[0]);
        Assert.Equal(0x00, NdefRecord.Uri("geo:-6.2,106.8").Payload[0]);

        var text = new NdefMessage([NdefRecord.Text("Hello", "en")]);
        Assert.Equal(H("D1 01 08 54 02 656E 48656C6C6F"), text.Encode());
        Assert.True(NdefMessage.Parse(text.Encode()).Records[0].TryGetText(out var t, out var lang));
        Assert.Equal(("Hello", "en"), (t, lang));
        // UTF-16 with a byte-order mark, as some phones write it.
        var utf16 = new NdefRecord(NdefTnf.WellKnown, "T"u8.ToArray(), [], [0x82, (byte)'i', (byte)'d', .. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("Halo")]);
        Assert.True(utf16.TryGetText(out var halo, out _));
        Assert.Equal("Halo", halo);
    }

    [Fact]
    public void Messages_with_several_record_kinds_round_trip()
    {
        var wifi = new WifiCredential("Plant-Commissioning", "not-a-real-key-123");
        var message = new NdefMessage(
        [
            NdefRecord.SmartPoster("https://docs.example.com/pump-7", "Pump 7 manual"),
            NdefRecord.Text("Asset P-0007, installed 2026-03-14"),
            NdefRecord.WifiCredential(wifi),
            NdefRecord.External("example.com:asset", "P-0007"u8),
            NdefRecord.AndroidApp("com.example.maintenance"),
            NdefRecord.Mime("application/json", Encoding.UTF8.GetBytes(new string('x', 300))),   // long record (SR = 0)
        ]);
        var bytes = message.Encode();
        var back = NdefMessage.Parse(bytes);
        Assert.Equal(message, back);
        Assert.True(back.Records[0].TryGetSmartPoster(out var sp));
        Assert.Equal("Text (en): Pump 7 manual", sp.Records[1].ToString());
        Assert.True(back.Records[2].TryGetWifiCredential(out var w));
        Assert.Equal(wifi, w);
        Assert.DoesNotContain("not-a-real-key", back.ToString(), StringComparison.Ordinal);   // keys are never shown
        Assert.Equal("Android app: com.example.maintenance", back.Records[4].ToString());
        Assert.Equal(0x00, bytes[^(300 + 16 + 4 + 2)] & 0x10);                                  // the last record is not short
        var fields = NdefMessage.Describe(bytes);
        Assert.Equal(6, fields.Count(f => f.Name.EndsWith("header", StringComparison.Ordinal)));
    }

    [Fact]
    public void Chunked_records_are_joined_and_malformed_messages_rejected()
    {
        // text/plain in three chunks: "abc" + "def" + "gh".
        var chunked = H("B2 0A 03 746578742F706C61696E 616263   36 00 03 646566   56 00 02 6768");
        var r = Assert.Single(NdefMessage.Parse(chunked).Records);
        Assert.Equal("text/plain", r.TypeText);
        Assert.Equal("abcdefgh", Encoding.ASCII.GetString(r.Payload));

        Assert.DoesNotContain(NdefMessage.Parse(H("D0 00 00")).Records, x => x.Tnf != NdefTnf.Empty);
        Assert.Throws<ProtocolException>(() => NdefMessage.Parse(H("51 01 01 54 00")));            // no MB
        Assert.Throws<ProtocolException>(() => NdefMessage.Parse(H("96 00 01 41")));               // unchanged outside a chunk
        Assert.Throws<ProtocolException>(() => NdefMessage.Parse(H("D1 01 08 55 02 6E78")));       // truncated
        Assert.Throws<ProtocolException>(() => NdefMessage.Parse(H("D7 00 00")));                  // TNF 7
        Assert.Throws<ProtocolException>(() => NdefMessage.Parse(H("91 01 01 54 00")));            // no ME
        Assert.Throws<ProtocolException>(() => NdefMessage.Parse(H("B2 01 01 41 00 56 01 01 42 00"))); // a chunk with a type
    }
}

public class Type2TagTests
{
    private static readonly byte[] Uid = [0x04, 0xA2, 0x4B, 0x1A, 0x2C, 0x5E, 0x80];

    private static (VirtualNfcReader Reader, VirtualType2Tag Tag) Setup(NdefMessage? message = null, Type2TagKind kind = Type2TagKind.Ntag213, bool writable = true)
    {
        var reader = new VirtualNfcReader();
        var tag = new VirtualType2Tag(kind, Uid, message, writable);
        reader.Present(tag);
        return (reader, tag);
    }

    [Fact]
    public void Memory_layout_follows_the_type_2_specification()
    {
        var pages = Type2Tag.UidPages(Uid);
        Assert.Equal(0x88 ^ 0x04 ^ 0xA2 ^ 0x4B, pages[3]);                                       // BCC0
        Assert.Equal(0x1A ^ 0x2C ^ 0x5E ^ 0x80, pages[8]);                                       // BCC1
        Assert.Equal(new byte[] { 0xE1, 0x10, 0x12, 0x00 }, Type2Tag.CapabilityContainer(Type2TagKind.Ntag213));
        Assert.Equal(0x6D, Type2Tag.CapabilityContainer(Type2TagKind.Ntag216)[2]);

        var area = Type2Tag.FormatDataArea(new NdefMessage([NdefRecord.Uri("https://www.nxp.com")]), 144);
        Assert.Equal(Convert.FromHexString("030CD101085502"), area[..7]);
        Assert.Equal(0xFE, area[14]);
        var big = Type2Tag.FormatDataArea(new NdefMessage([NdefRecord.Text(new string('a', 400))]), 872);
        Assert.Equal(new byte[] { 0x03, 0xFF, 0x01, 0x9A }, big[..4]);                            // 3-byte TLV length: 410 (long record header)
        Assert.Throws<ArgumentException>(() => Type2Tag.FormatDataArea(new NdefMessage([NdefRecord.Text(new string('a', 200))]), 144));

        // Lock-control TLV and NULL padding before the NDEF TLV, as on factory-formatted MIFARE Ultralight C.
        var withLock = Convert.FromHexString("0103A0100400" + "0310D1010C5502" + "6578616D706C652E636F6D" + "FE");
        Assert.Equal("https://www.example.com", Type2Tag.ReadNdef(withLock)!.Records[0].TryGetUri(out var u) ? u : "");
        Assert.Throws<ProtocolException>(() => Type2Tag.ReadNdef(Convert.FromHexString("0340D101")));
    }

    [Fact]
    public async Task Reads_writes_and_refuses_through_the_pcsc_commands()
    {
        var (reader, tag) = Setup(new NdefMessage([NdefRecord.Uri("https://www.nxp.com")]));
        await using var card = await reader.WaitForTagAsync();
        var client = new Type2TagClient(card);
        Assert.Equal(Uid, await client.GetUidAsync());
        Assert.Equal("URI: https://www.nxp.com", (await client.ReadNdefAsync())!.ToString());

        var message = new NdefMessage([NdefRecord.Text("Serviced 2026-10-09 by technician 14"), NdefRecord.Uri("https://docs.example.com/p7")]);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => client.WriteNdefAsync(message));
        var writer = new Type2TagClient(card, new NfcTagOptions().AllowWrites());
        await writer.WriteNdefAsync(message);
        Assert.Equal(message, await client.ReadNdefAsync());
        var written = tag.PagesWritten;
        await writer.WriteNdefAsync(message);
        Assert.Equal(written, tag.PagesWritten);                                                  // nothing changed, nothing written
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.WritePageAsync(2, new byte[4]));

        var map = Type2Tag.Map(await client.DumpAsync(), 144);
        Assert.Equal(Type2Region.Uid, map[0]);
        Assert.Equal(Type2Region.CapabilityContainer, map[12]);
        Assert.Equal(Type2Region.TlvHeader, map[16]);
        Assert.Equal(Type2Region.Ndef, map[18]);
        Assert.Contains(Type2Region.Terminator, map);
        Assert.Equal(Type2Region.Configuration, map[^1]);

        reader.Remove();
        await Assert.ThrowsAsync<TransportException>(() => client.ReadNdefAsync());
    }

    [Fact]
    public async Task Read_only_tags_large_tags_and_blank_tags()
    {
        var (reader, _) = Setup(new NdefMessage([NdefRecord.Text("Locked")]), writable: false);
        await using (var card = await reader.WaitForTagAsync())
        {
            var writer = new Type2TagClient(card, new NfcTagOptions().AllowWrites());
            await Assert.ThrowsAsync<DeviceException>(() => writer.WriteNdefAsync(new NdefMessage([NdefRecord.Text("x")])));
        }

        var (big, _) = Setup(kind: Type2TagKind.Ntag216);
        await using (var card = await big.WaitForTagAsync())
        {
            var client = new Type2TagClient(card, new NfcTagOptions().AllowWrites());
            Assert.Empty((await client.ReadNdefAsync())!.Records);                                 // blank tag: empty NDEF TLV
            var longText = new NdefMessage([NdefRecord.Text(new string('z', 700))]);
            await client.WriteNdefAsync(longText);
            Assert.Equal(longText, await client.ReadNdefAsync());
        }

        // WaitForTag blocks until a tag is presented.
        var empty = new VirtualNfcReader();
        var waiting = empty.WaitForTagAsync().AsTask();
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        empty.Present(new VirtualType2Tag(Type2TagKind.Ntag215, Uid));
        await using var late = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Uid, await new Type2TagClient(late).GetUidAsync());
    }

    [Fact]
    public void Pcsc_lists_readers_or_explains_why_not()
    {
        try
        {
            var readers = Pcsc.ListReaders();
            Assert.NotNull(readers);
        }
        catch (PcscException ex)
        {
            Assert.False(string.IsNullOrEmpty(ex.Message));
        }
    }
}

public class NfcConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("ndef.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var data = v.Hex("data");
        var expected = v.GetProperty("fields").GetString();
        var tag = v.GetProperty("kind").GetString() == "type2";
        if (expected == "error")
        {
            Assert.ThrowsAny<ProtocolException>(() => tag ? Type2Tag.ReadNdef(data) : NdefMessage.Parse(data));
            return;
        }

        var message = tag ? Type2Tag.ReadNdef(data)! : NdefMessage.Parse(data);
        var rendered = string.Join(";", message.Records.Select(r => $"{(byte)r.Tnf}|{r.TypeText}|{Encoding.ASCII.GetString(r.Id)}|{Convert.ToHexString(r.Payload)}"));
        Assert.Equal(expected, rendered);
        if (!tag && v.TryGetProperty("canonical", out var canonical) && canonical.GetBoolean()) Assert.Equal(data, message.Encode());
    }
}
