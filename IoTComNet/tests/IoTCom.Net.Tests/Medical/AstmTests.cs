using System.Text;
using IoTCom.Net.Protocols.Astm;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Medical;

public class AstmTests
{
    private const string Sample =
        "H|\\^&|||CHEM-01^2.1|||||||P|LIS2-A2|20261009081500\r" +
        "P|1|P12345|||SANTOSO^Budi||19700412|M\r" +
        "O|1|S2026-0001||^^^GLU\\^^^K|R||20261009074000\r" +
        "R|1|^^^GLU|142|mg/dL|70 to 99|H||F||||20261009081200\r" +
        "R|2|^^^K|4.1|mmol/L|3.5 to 5.1|N||F||||20261009081200\r" +
        "C|1|I|Synthetic|G\r" +
        "L|1|N\r";

    [Fact]
    public void Records_and_results_parse()
    {
        var m = AstmMessage.Parse(Sample);
        Assert.Equal("CHEM-01", m.Sender);
        Assert.Equal("P12345", m.PatientId);
        Assert.Equal("SANTOSO^Budi", m.PatientName);
        Assert.Equal("S2026-0001", m.SpecimenId);
        Assert.Equal(["GLU", "K"], m.Results.Select(r => r.TestCode));
        var glu = m.Results[0];
        Assert.Equal((142.0, "mg/dL", "H", "F"), (glu.Number!.Value, glu.Units, glu.Flag, glu.Status));
        Assert.Equal(new DateTime(2026, 10, 9, 8, 12, 0), glu.Completed);
        Assert.Equal(Sample, m.Encode());
    }

    [Fact]
    public void Custom_delimiters_come_from_the_header()
    {
        var m = AstmMessage.Parse("H#~!$###LAB\rR#1#!!!NA#139#mmol/L#135 to 145#N\rL#1#N\r");
        Assert.Equal('#', m.Header.Delimiters.Field);
        Assert.Equal("NA", m.Results.Single().TestCode);
        Assert.False(AstmMessage.TryParse("P|1|x\rL|1\r", out _));
    }

    [Fact]
    public void Frames_carry_checksums_and_split_long_records()
    {
        var frame = AstmLink.Frame(7, "L|1|N\r", last: true);
        var sum = (Encoding.ASCII.GetBytes("7L|1|N\r").Sum(b => b) + 0x03) & 0xFF;
        Assert.Equal(sum.ToString("X2"), Encoding.ASCII.GetString(frame[^4..^2]));
        Assert.True(AstmLink.TryParseFrame(frame, out var fn, out var text, out var last, out _));
        Assert.Equal((7, "L|1|N\r", true), (fn, text, last));
        frame[3] ^= 1;
        Assert.False(AstmLink.TryParseFrame(frame, out _, out _, out _, out var error));
        Assert.Contains("Checksum", error, StringComparison.Ordinal);

        var longComment = new AstmMessageBuilder().Header("X").Comment(new string('a', 500)).Build();
        var frames = AstmLink.Frames(longComment);
        Assert.Equal(1 + 3 + 1, frames.Count);                       // H, C split in three (ETB, ETB, ETX), L
        Assert.Equal(AstmLink.Etb, frames[1][^5]);
        Assert.Equal(AstmLink.Etx, frames[3][^5]);
        Assert.Equal([1, 2, 3, 4, 5], frames.Select(f => f[1] - '0'));
    }

    [Fact]
    public async Task Transfer_with_a_corrupted_frame_is_retransmitted()
    {
        var link = new InMemoryTransportListener("astm");
        await using var lis = AstmReceiver.Create(o => o.ListenInMemory(link));
        await lis.StartAsync();
        await using var analyzer = AstmSender.Create(o => { o.UseInMemory(link); o.AckTimeout = TimeSpan.FromSeconds(2); });
        await analyzer.ConnectAsync();
        var received = new TaskCompletionSource<AstmMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lis.MessageReceived += (_, m) => received.TrySetResult(m);

        var message = new AnalyzerSimulator().NextResult(new DateTime(2026, 10, 9, 8, 0, 0));
        analyzer.CorruptNextFrame = true;
        await analyzer.SendAsync(message);
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, analyzer.Retransmissions);
        Assert.Equal(1, lis.NakCount);
        Assert.Equal(message.Encode(), got.Encode());
        Assert.Equal(7, got.Results.Count);
    }

    [Fact]
    public void Bridge_produces_an_oru()
    {
        var oru = AstmToHl7.ToOru(AstmMessage.Parse(Sample));
        var parsed = Hl7Message.Parse(oru.Encode());
        Assert.Equal("ORU^R01", parsed.MessageType);
        Assert.Equal("P12345", parsed.Get("PID.3.1"));
        var obx = parsed.Segments.Where(s => s.Name == "OBX").ToList();
        Assert.Equal(2, obx.Count);
        Assert.Equal("142", parsed.Get("OBX.5"));
        Assert.Equal("H", parsed.Get("OBX.8"));
        Assert.Equal("70-99", parsed.Get("OBX.7"));
    }
}
