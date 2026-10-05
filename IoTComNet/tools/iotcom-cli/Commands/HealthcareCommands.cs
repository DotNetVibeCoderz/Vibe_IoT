using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Adapters.Dicom;
using IoTCom.Net.Protocols.Hl7;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal sealed class Hl7ListenCommand : AsyncCommand<Hl7ListenCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port"), Description("MLLP port (default 2575).")] public int Port { get; init; } = 2575;
        [CommandOption("--raw"), Description("Print the full message, segment by segment.")] public bool Raw { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var server = Hl7MllpServer.Create(o => o.UseTcp(IPAddress.Any, s.Port));
        server.MessageReceived += (_, e) =>
        {
            var m = e.Message;
            var patient = m.GetPatient();
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] [{Ui.Hex(Ui.Amber)}]{Markup.Escape(m.MessageType)}[/] {Markup.Escape(m.ControlId)} " +
                $"[{Ui.Hex(Ui.CableBlue)}]{Markup.Escape(m.Sender.Application)}[/] → ACK AA" + (patient is null ? "" : $"  [bold]{Markup.Escape(patient.DisplayName)}[/] ({Markup.Escape(patient.Id)})"));
            foreach (var o in m.GetObservations())
            {
                var flag = o.AbnormalFlag is "H" or "L" or "HH" or "LL" or "A" ? $"[{Ui.Hex(Ui.Fault)}]{o.AbnormalFlag}[/]" : $"[{Ui.Hex(Ui.Muted)}]{o.AbnormalFlag}[/]";
                AnsiConsole.MarkupLine($"    {Markup.Escape(o.Code.Text),-36} {Markup.Escape(o.Value),8} {Markup.Escape(o.Units),-8} {flag}");
            }
            if (s.Raw) foreach (var seg in m.Segments) AnsiConsole.MarkupLine($"    [{Ui.Hex(Ui.Muted)}]{Markup.Escape(seg.ToString())}[/]");
        };
        await server.StartAsync(ct);
        Ui.Success($"HL7 MLLP receiver on tcp://0.0.0.0:{s.Port} (auto-ACK). Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after {server.MessagesReceived} messages.");
        return 0;
    }
}

internal sealed class Hl7SendCommand : AsyncCommand<Hl7SendCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[file]"), Description("ER7 file to send (segments separated by CR or LF). Omit to send a sample ORU^R01.")] public string? File { get; init; }
        [CommandOption("-h|--host")] public string Host { get; init; } = "127.0.0.1";
        [CommandOption("-p|--port")] public int Port { get; init; } = 2575;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        Hl7Message message;
        if (s.File is not null) message = Hl7Message.Parse(await File.ReadAllTextAsync(s.File, ct));
        else
        {
            var (patient, scenario, bed) = PatientMonitorSimulator.DemoWard[1];
            var sim = new PatientMonitorSimulator(patient, scenario, bed);
            message = sim.ToOru(sim.Next(TimeSpan.FromSeconds(5), DateTimeOffset.Now));
        }
        await using var client = Hl7MllpClient.Create(o => o.UseTcp(s.Host, s.Port));
        var ack = await client.SendAsync(message, throwOnNegativeAck: false, ct);
        var ok = ack.IsPositiveAck();
        (ok ? (Action<string>)Ui.Success : Ui.Error)($"{Markup.Escape(message.MessageType)} {Markup.Escape(message.ControlId)} → ACK [bold]{Markup.Escape(ack.AckCode())}[/] {Markup.Escape(ack.Get("MSA.3"))}");
        return ok ? 0 : 2;
    }
}

internal sealed class Hl7SimulateCommand : AsyncCommand<Hl7SimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-h|--host")] public string Host { get; init; } = "127.0.0.1";
        [CommandOption("-p|--port")] public int Port { get; init; } = 2575;
        [CommandOption("--scenario"), Description("stable, sepsis, hypoxia or hypertension (default sepsis).")] public string Scenario { get; init; } = "sepsis";
        [CommandOption("--interval"), Description("Seconds between ORU^R01 messages (default 5).")] public double Interval { get; init; } = 5;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var scenario = Enum.Parse<PatientScenario>(s.Scenario, ignoreCase: true);
        var (patient, _, bed) = PatientMonitorSimulator.DemoWard[0];
        var sim = new PatientMonitorSimulator(patient, scenario, bed);
        await using var client = Hl7MllpClient.Create(o => o.UseTcp(s.Host, s.Port));
        await client.SendAsync(sim.Admission(DateTimeOffset.Now), ct: ct);
        Ui.Success($"Bedside monitor (synthetic, {scenario}) sending ORU^R01 to {s.Host}:{s.Port} every {s.Interval:0.#} s. Ctrl+C to stop.");
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(s.Interval));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var v = sim.Next(TimeSpan.FromSeconds(s.Interval), DateTimeOffset.Now);
                await client.SendAsync(sim.ToOru(v), ct: ct);
                AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{Ui.Hex(Ui.Muted)}]{v.Time:HH:mm:ss}[/] HR {v.HeartRate:0}  RR {v.RespiratoryRate:0}  SpO₂ {v.SpO2:0}%  BP {v.Systolic:0}/{v.Diastolic:0}  T {v.Temperature:0.0}°C  [{Ui.Hex(Ui.LampGreen)}]ACK AA[/]"));
            }
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}

internal sealed class DicomEchoCommand : AsyncCommand<DicomEchoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-h|--host")] public string Host { get; init; } = "127.0.0.1";
        [CommandOption("-p|--port")] public int Port { get; init; } = 11112;
        [CommandOption("--aet"), Description("Our AE title.")] public string CallingAe { get; init; } = "IOTCOM-SCU";
        [CommandOption("--aec"), Description("Remote AE title.")] public string CalledAe { get; init; } = "ANY-SCP";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var scu = DicomStoreClient.Create(o => { o.Host = s.Host; o.Port = s.Port; o.CallingAe = s.CallingAe; o.CalledAe = s.CalledAe; });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scu.EchoAsync(ct);
        Ui.Success($"C-ECHO {Markup.Escape(s.CalledAe)}@{s.Host}:{s.Port} OK in {sw.ElapsedMilliseconds} ms.");
        return 0;
    }
}

internal sealed class DicomSendCommand : AsyncCommand<DicomSendCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[file]"), Description("DICOM file to send. Omit to send a synthetic study.")] public string? File { get; init; }
        [CommandOption("-h|--host")] public string Host { get; init; } = "127.0.0.1";
        [CommandOption("-p|--port")] public int Port { get; init; } = 11112;
        [CommandOption("--aec"), Description("Remote AE title.")] public string CalledAe { get; init; } = "ANY-SCP";
        [CommandOption("--synthetic"), Description("ct, mr or xray (default xray).")] public string Synthetic { get; init; } = "xray";
        [CommandOption("--finding"), Description("Planted finding, e.g. LungNodule, Pneumothorax, BrainMass (default random).")] public string? Finding { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        FellowOakDicom.DicomFile file;
        var label = s.File ?? "";
        if (s.File is not null) file = await FellowOakDicom.DicomFile.OpenAsync(s.File);
        else
        {
            var modality = s.Synthetic.ToLowerInvariant() switch { "ct" => SyntheticModality.ChestCt, "mr" or "mri" => SyntheticModality.BrainMr, _ => SyntheticModality.ChestXray };
            var options = SyntheticImaging.FindingsFor(modality);
            var finding = s.Finding is null ? options[Random.Shared.Next(options.Count)] : Enum.Parse<SyntheticFinding>(s.Finding, ignoreCase: true);
            var study = SyntheticImaging.Generate(modality, finding, seed: Random.Shared.Next());
            file = study.File;
            label = $"synthetic {modality} ({finding})";
        }
        await using var scu = DicomStoreClient.Create(o => { o.Host = s.Host; o.Port = s.Port; o.CalledAe = s.CalledAe; });
        var status = await scu.StoreAsync(file, ct);
        Ui.Success($"C-STORE {Markup.Escape(label)} → {Markup.Escape(s.CalledAe)}@{s.Host}:{s.Port}: {Markup.Escape(status.ToString())}");
        return 0;
    }
}

internal sealed class DicomListenCommand : AsyncCommand<DicomListenCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port")] public int Port { get; init; } = 11112;
        [CommandOption("--aet"), Description("Our AE title.")] public string AeTitle { get; init; } = "IOTCOM-SCP";
        [CommandOption("-o|--output"), Description("Folder to save received objects (.dcm) and PNG previews.")] public string? Output { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (s.Output is not null) Directory.CreateDirectory(s.Output);
        await using var scp = DicomStoreServer.Create(o => { o.Port = s.Port; o.AeTitle = s.AeTitle; });
        scp.ImageReceived += r =>
        {
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] C-STORE [{Ui.Hex(Ui.Amber)}]{Markup.Escape(r.Modality)}[/] {Markup.Escape(r.StudyDescription)} " +
                $"[{Ui.Hex(Ui.CableBlue)}]{Markup.Escape(r.PatientName)}[/] from {Markup.Escape(r.CallingAe)}");
            if (s.Output is null) return;
            var name = Path.Combine(s.Output, r.SopInstanceUid);
            r.File.Save(name + ".dcm");
            try { File.WriteAllBytes(name + ".png", DicomRenderer.Render(r.File.Dataset).ToPng()); }
            catch (NotSupportedException) { /* compressed or colour: keep the .dcm only */ }
        };
        await scp.StartAsync(ct);
        Ui.Success($"DICOM Storage SCP {Markup.Escape(s.AeTitle)} on port {s.Port}{(s.Output is null ? "" : $", saving to {Markup.Escape(s.Output)}")}. Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        Ui.Warn($"Stopped after {scp.ImagesReceived} objects.");
        return 0;
    }
}
