// Hl7MllpListener — receive HL7 v2 over MLLP (auto-ACK) and print observations with abnormal flags.
//
//   dotnet run -- --simulate              # also starts a synthetic bedside monitor (sepsis course) sending ORU^R01
//   dotnet run -- --port 2575             # listen for real devices / interface engines
//
// All simulated patients are fictional. Not a medical device.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Hl7;

var port = Array.IndexOf(args, "--port") is var i and >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : 2575;
var simulate = args.Contains("--simulate") || args.Length == 0;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var receiver = Hl7MllpServer.Create(o => o.UseTcp(IPAddress.Any, port));

// Business rule example: reject results for unknown patients with AE, accept everything else (auto AA).
receiver.MessageReceived += (_, e) =>
{
    var m = e.Message;
    var patient = m.GetPatient();
    if (m.MessageType.StartsWith("ORU", StringComparison.Ordinal) && string.IsNullOrEmpty(patient?.Id))
    {
        e.Ack = m.CreateAck("AE", "PID-3 patient identifier is required");
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m.MessageType} {m.ControlId}  → AE (no patient id)");
        return;
    }
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m.MessageType,-8} {m.ControlId}  from {m.Sender.Application}/{m.Sender.Facility}  {patient?.DisplayName} ({patient?.Id})");
    foreach (var o in m.GetObservations())
    {
        var abnormal = o.AbnormalFlag is "H" or "L" or "HH" or "LL" or "A";
        Console.WriteLine($"          {o.Code.Text,-36} {o.Value,7} {o.Units,-8} {(abnormal ? "◀ " + o.AbnormalFlag : "")}");
    }
};
await receiver.StartAsync(cts.Token);
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · HL7 MLLP receiver on tcp://0.0.0.0:{port}. Ctrl+C to stop.\n");

if (simulate)
{
    // A synthetic bedside monitor on the same machine, deteriorating into sepsis after one minute.
    var (patient, _, bed) = PatientMonitorSimulator.DemoWard[0];
    var monitor = new PatientMonitorSimulator(patient, PatientScenario.Sepsis, bed, onset: TimeSpan.FromMinutes(1));
    _ = Task.Run(async () =>
    {
        await using var sender = Hl7MllpClient.Create(o => o.UseTcp("127.0.0.1", port));
        await sender.SendAsync(monitor.Admission(DateTimeOffset.Now), ct: cts.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var simTime = DateTimeOffset.Now;
        while (await timer.WaitForNextTickAsync(cts.Token))
        {
            simTime = simTime.AddSeconds(20); // 10× faster than real time
            await sender.SendAsync(monitor.ToOru(monitor.Next(TimeSpan.FromSeconds(20), simTime)), ct: cts.Token);
        }
    }, cts.Token);
}

try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
Console.WriteLine($"Received {receiver.MessagesReceived} messages.");
