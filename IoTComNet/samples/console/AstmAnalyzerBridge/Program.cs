// AstmAnalyzerBridge — receive results from a lab analyzer over ASTM (E1381/E1394) and forward them to a LIS or
// HIS as HL7 v2.5.1 ORU^R01 over MLLP. Synthetic data only; not a medical device.
//
//   dotnet run                          # analyzer simulator → bridge → HL7 receiver, all in this process
//   dotnet run -- 5000 10.0.0.20 2575   # real analyzer on TCP 5000, HL7 to 10.0.0.20:2575
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Astm;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Transports;

var demo = args.Length == 0;
var analyzerLink = new InMemoryTransportListener("analyzer");
var hl7Link = new InMemoryTransportListener("his");

// The HIS side (only in the demo): an HL7 receiver that prints what arrives.
await using var his = demo ? Hl7MllpServer.Create(o => o.ListenInMemory(hl7Link)) : null;
if (his is not null)
{
    his.MessageReceived += (_, e) => Console.WriteLine($"  HIS ← {e.Message.MessageType} · {e.Message.GetSegments("OBX").Count()} observations · ACK sent");
    await his.StartAsync();
}

// The bridge: ASTM receiver in, HL7 sender out.
await using var astm = AstmReceiver.Create(o =>
{
    if (demo) o.ListenInMemory(analyzerLink);
    else o.UseTcp(IPAddress.Any, int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture));
});
await using var hl7 = Hl7MllpClient.Create(o =>
{
    if (demo) o.UseInMemory(hl7Link);
    else o.UseTcp(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
});
await hl7.ConnectAsync();
astm.MessageReceived += async (_, message) =>
{
    Console.WriteLine($"ASTM ← {message.Sender}: specimen {message.SpecimenId}, {string.Join(", ", message.Results.Select(r => $"{r.TestCode} {r.Value}{(r.Flag is "N" ? "" : " " + r.Flag)}"))}");
    var ack = await hl7.SendAsync(AstmToHl7.ToOru(message));
    Console.WriteLine($"  bridge → ORU^R01, ACK {ack.AckCode()}");
};
await astm.StartAsync();
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · ASTM → HL7 bridge (synthetic data, not a medical device)\n");

if (demo)
{
    // The analyzer: three specimens, one of them with a corrupted frame that the link retransmits.
    await using var analyzer = AstmSender.Create(o => o.UseInMemory(analyzerLink));
    await analyzer.ConnectAsync();
    var chemistry = new AnalyzerSimulator();
    for (var i = 0; i < 3; i++)
    {
        analyzer.CorruptNextFrame = i == 1;
        await analyzer.SendAsync(chemistry.NextResult());
        await Task.Delay(300);
    }

    Console.WriteLine($"\n{astm.MessagesReceived} messages bridged; {astm.NakCount} frame(s) NAKed and retransmitted.");
}
else
{
    Console.WriteLine("Waiting for the analyzer… Ctrl+C to stop.");
    await Task.Delay(Timeout.Infinite);
}

Console.WriteLine(IoTComInfo.CreditEn);
