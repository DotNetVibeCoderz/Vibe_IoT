using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IoTCom.Net.Adapters.Dicom;
using IoTCom.Net.Framing;
using IoTCom.Net.Protocols.Hl7;
using IoTCom.Net.Transports;
using IoTCom.Samples.Medical;

namespace IoTCom.Net.Tests.Medical;

public class Hl7Tests
{
    // Classic ORU^R01 example with escape sequences and repeated OBX.
    private const string Oru =
        "MSH|^~\\&|MONITOR|ICU-01|LIS|HOSP|20260105083000+0700||ORU^R01^ORU_R01|MSG00001|P|2.5.1\r" +
        "PID|1||MRN-1001^^^IOTCOM^MR||Santoso^Budi||19580412|M\r" +
        "OBR|1|||VITALS^Vital signs panel^L\r" +
        "OBX|1|NM|8867-4^Heart rate^LN||118|/min|60-100|H|||F|||20260105083000+0700\r" +
        "OBX|2|NM|59408-5^Oxygen saturation^LN||93|%|95-100|L|||F\r" +
        "NTE|1||Line one\\.br\\Pipe \\F\\ caret \\S\\ amp \\T\\ done\r";

    [Fact]
    public void Parses_header_patient_and_observations()
    {
        var m = Hl7Message.Parse(Oru);
        Assert.Equal("ORU^R01", m.MessageType);
        Assert.Equal("MSG00001", m.ControlId);
        Assert.Equal("2.5.1", m.Version);
        Assert.Equal(("MONITOR", "ICU-01"), m.Sender);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 8, 30, 0, TimeSpan.FromHours(7)), m.Timestamp);

        var p = m.GetPatient()!;
        Assert.Equal("MRN-1001", p.Id);
        Assert.Equal("Santoso, Budi", p.DisplayName);
        Assert.Equal(new DateOnly(1958, 4, 12), p.BirthDate);

        var obs = m.GetObservations();
        Assert.Equal(2, obs.Count);
        Assert.Equal(VitalSigns.HeartRate.Identifier, obs[0].Code.Identifier);
        Assert.Equal(118, obs[0].NumericValue);
        Assert.Equal("H", obs[0].AbnormalFlag);
        Assert.Equal("93", m["OBX(2).5"]);
        Assert.Equal("Line one\nPipe | caret ^ amp & done", m["NTE.3"]);
    }

    [Fact]
    public void Builder_escapes_and_roundtrips()
    {
        var m = new Hl7MessageBuilder()
            .Header("APP", "FAC", "RCV", "RFAC", "ADT^A01", controlId: "42")
            .Patient(new Hl7Patient { Id = "X|1", FamilyName = "O'Neil & Sons", GivenName = "Ann", Sex = "F" })
            .Build();
        var parsed = Hl7Message.Parse(m.Encode());
        Assert.Equal("ADT^A01", parsed.MessageType);
        Assert.Equal("X|1", parsed.GetPatient()!.Id);
        Assert.Equal("O'Neil & Sons", parsed.GetPatient()!.FamilyName);
        Assert.Contains("X\\F\\1", m.Encode(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ack_references_the_original_message()
    {
        var ack = Hl7Message.Parse(Oru).CreateAck("AE", "Unknown bed");
        Assert.Equal("ACK^R01", ack.MessageType);
        Assert.Equal("AE", ack.AckCode());
        Assert.Equal("MSG00001", ack["MSA.2"]);
        Assert.Equal("Unknown bed", ack["MSA.3"]);
        Assert.Equal(("LIS", "HOSP"), ack.Sender);
        Assert.False(ack.IsPositiveAck());
    }

    [Fact]
    public void Rejects_text_without_msh() => Assert.False(Hl7Message.TryParse("PID|1||X", out _));

    [Fact]
    public void Mllp_framing_handles_noise_and_fragments()
    {
        var mllp = new MllpFraming();
        var wire = new ArrayBufferWriter<byte>();
        wire.Write("garbage"u8);
        mllp.Encode(Encoding.ASCII.GetBytes("MSH|A"), wire);
        mllp.Encode(Encoding.ASCII.GetBytes("MSH|B"), wire);
        var frames = Framing.CobsTests.DecodeFragmented(mllp, wire.WrittenSpan.ToArray(), fragment: 3);
        Assert.Equal(["MSH|A", "MSH|B"], frames.Select(f => Encoding.ASCII.GetString(f)));
    }

    [Fact]
    public async Task Client_and_server_exchange_messages_with_acks()
    {
        var link = new InMemoryTransportListener();
        await using var server = Hl7MllpServer.Create(o => o.ListenInMemory(link));
        server.MessageReceived += (_, e) => { if (e.Message.GetPatient()?.Id == "REJECT") e.Ack = e.Message.CreateAck("AR", "Rejected by test"); };
        await server.StartAsync();
        await using var client = Hl7MllpClient.Create(o => o.UseInMemory(link));

        var (patient, scenario, bed) = PatientMonitorSimulator.DemoWard[0];
        var sim = new PatientMonitorSimulator(patient, scenario, bed);
        var ack = await client.SendAsync(sim.Admission(DateTimeOffset.Now));
        Assert.True(ack.IsPositiveAck());
        for (var i = 0; i < 20; i++) await client.SendAsync(sim.ToOru(sim.Next(TimeSpan.FromSeconds(5), DateTimeOffset.Now)));
        Assert.Equal(21, server.MessagesReceived);

        var reject = new Hl7MessageBuilder().Header("A", "B", "C", "D", "ORU^R01").Patient(new Hl7Patient { Id = "REJECT" }).Build();
        var ex = await Assert.ThrowsAsync<DeviceException>(() => client.SendAsync(reject));
        Assert.Contains("AR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sepsis_course_deteriorates_and_roundtrips_through_oru()
    {
        var (patient, _, bed) = PatientMonitorSimulator.DemoWard[0];
        var sim = new PatientMonitorSimulator(patient, PatientScenario.Sepsis, bed, seed: 1, onset: TimeSpan.Zero);
        var first = sim.Next(TimeSpan.FromSeconds(5), DateTimeOffset.Now);
        VitalsSample last = first;
        for (var i = 0; i < 140; i++) last = sim.Next(TimeSpan.FromSeconds(5), DateTimeOffset.Now);
        Assert.True(last.HeartRate > first.HeartRate + 25);
        Assert.True(last.Systolic < first.Systolic - 20);
        Assert.True(last.Temperature > 38);

        var decoded = PatientMonitorSimulator.FromOru(Hl7Message.Parse(sim.ToOru(last).Encode()));
        Assert.Equal(last.HeartRate, decoded.HeartRate);
        Assert.Equal(last.Temperature, decoded.Temperature);
    }
}

public class ClinicalAnalysisTests
{
    [Theory]
    [InlineData(16, 98, 120, 75, 36.8, 0, RiskLevel.Low)]
    [InlineData(26, 92, 88, 135, 39.5, 3 + 2 + 3 + 3 + 2, RiskLevel.High)]
    [InlineData(22, 95, 115, 95, 37.0, 2 + 1 + 0 + 1 + 0, RiskLevel.Low)]
    [InlineData(8, 97, 120, 70, 37.0, 3, RiskLevel.LowMedium)]
    [InlineData(21, 94, 105, 92, 38.5, 2 + 1 + 1 + 1 + 1, RiskLevel.Medium)]
    public void News2_matches_the_rcp_tables(double rr, double spo2, double sbp, double hr, double temp, int expected, RiskLevel risk)
    {
        var s = VitalsAnalyzer.News2(rr, spo2, sbp, hr, temp);
        Assert.Equal(expected, s.Total);
        Assert.Equal(risk, s.Risk);
    }

    [Fact]
    public void Analyzer_detects_deterioration_and_projects_it()
    {
        var (patient, _, bed) = PatientMonitorSimulator.DemoWard[0];
        var sim = new PatientMonitorSimulator(patient, PatientScenario.Sepsis, bed, seed: 2, onset: TimeSpan.FromMinutes(1));
        var analyzer = new VitalsAnalyzer();
        var t = DateTimeOffset.Now;
        for (var i = 0; i < 120; i++) analyzer.Add(sim.Next(TimeSpan.FromSeconds(5), t = t.AddSeconds(5)));
        var snap = analyzer.Analyze(patient, bed);
        Assert.True(snap.News2.Total >= 5);
        Assert.True(snap.ProjectedNews2.Total >= snap.News2.Total);
        Assert.True(snap.Trends.Single(x => x.Name == "Heart rate").SlopePerHour > 0);
        Assert.NotEmpty(snap.Alerts);
        Assert.Contains("NEWS2", ClinicalAssistant.Describe(snap, analyzer.History), StringComparison.Ordinal);
        Assert.Contains("**Situation**", ClinicalAssistant.Template(snap, "en"), StringComparison.Ordinal);
    }

    [Fact]
    public void Ground_truth_matching_respects_negation()
    {
        static ImagingReport R(string text) => new([text], "", "high", false, "test", null);
        Assert.True(ClinicalAssistant.Mentions(R("Large right pneumothorax with collapsed lung."), SyntheticFinding.Pneumothorax));
        Assert.False(ClinicalAssistant.Mentions(R("No pleural effusion or pneumothorax."), SyntheticFinding.Pneumothorax));
        Assert.False(ClinicalAssistant.Mentions(R("Lungs clear, without effusion."), SyntheticFinding.PleuralEffusion));
        Assert.True(ClinicalAssistant.Mentions(R("Normal study. No acute abnormality."), SyntheticFinding.None));
        Assert.False(ClinicalAssistant.Mentions(R("Cardiomegaly."), SyntheticFinding.None));
        Assert.True(ClinicalAssistant.Mentions(R("Tidak ada efusi. Tampak infark di wilayah MCA kanan."), SyntheticFinding.Infarct));
    }

    [Fact]
    public void Report_parser_handles_fenced_and_truncated_json()
    {
        var full = ClinicalAssistant.ParseReport("```json\n{\"findings\":[\"Nodule right upper lobe\"],\"impression\":\"Nodule\",\"confidence\":\"high\",\"urgent\":false}\n```", "t");
        Assert.Equal("Nodule right upper lobe", Assert.Single(full.Findings));
        Assert.Equal("high", full.Confidence);
        var partial = ClinicalAssistant.ParseReport("{\"findings\":[\"Wedge-shaped hyperintensity in the right MCA territory\",\"No midline shift observed", "t");
        Assert.Contains(partial.Findings, f => f.Contains("MCA", StringComparison.Ordinal));
    }
}

public class DicomTests
{
    [Theory]
    [InlineData(SyntheticModality.ChestCt, SyntheticFinding.LungNodule, "CT", 512)]
    [InlineData(SyntheticModality.BrainMr, SyntheticFinding.Infarct, "MR", 256)]
    [InlineData(SyntheticModality.ChestXray, SyntheticFinding.PleuralEffusion, "DX", 768)]
    public void Synthetic_studies_are_valid_dicom_and_render(SyntheticModality m, SyntheticFinding f, string modality, int size)
    {
        var study = SyntheticImaging.Generate(m, f);
        using var ms = new MemoryStream();
        study.File.Save(ms);
        ms.Position = 0;
        var reopened = FellowOakDicom.DicomFile.Open(ms);
        Assert.Equal(modality, reopened.Dataset.GetString(FellowOakDicom.DicomTag.Modality));
        Assert.Contains("SYNTHETIC", reopened.Dataset.GetString(FellowOakDicom.DicomTag.ImageComments), StringComparison.Ordinal);

        var img = DicomRenderer.Render(reopened.Dataset);
        Assert.Equal(size, img.Width);
        Assert.InRange(img.Pixels.Average(p => (double)p), 10, 245);   // not blank, not saturated
        var png = img.ToPng();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public void Planted_findings_change_the_image()
    {
        var normal = DicomRenderer.Render(SyntheticImaging.Generate(SyntheticModality.ChestCt, SyntheticFinding.None, seed: 3).File.Dataset);
        var ptx = DicomRenderer.Render(SyntheticImaging.Generate(SyntheticModality.ChestCt, SyntheticFinding.Pneumothorax, seed: 3).File.Dataset);
        var differing = normal.Pixels.Zip(ptx.Pixels).Count(p => Math.Abs(p.First - p.Second) > 30);
        Assert.True(differing > 5000);
    }

    [Fact]
    public async Task Cstore_and_cecho_roundtrip_over_tcp()
    {
        var port = FreePort();
        await using var scp = DicomStoreServer.Create(o => o.Port = port);
        await scp.StartAsync();
        var tap = new RecordingTap();
        scp.AddTap(tap);
        await using var scu = DicomStoreClient.Create(o => o.Port = port);
        await scu.ConnectAsync();
        Assert.Equal(EndpointState.Connected, scu.State);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stream = scp.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = stream.MoveNextAsync();
        var study = SyntheticImaging.Generate(SyntheticModality.ChestXray, SyntheticFinding.LungNodule, "TEST^PATIENT", "T-1");
        var status = await scu.StoreAsync(study.File);
        Assert.Equal(FellowOakDicom.Network.DicomState.Success, status.State);
        Assert.True(await next);
        Assert.Equal("DX", stream.Current.Modality);
        Assert.Equal("TEST PATIENT", stream.Current.PatientName);
        Assert.Equal(1, scp.ImagesReceived);
        Assert.Contains(tap.Snapshot(), f => f.Summary!.Contains("C-STORE", StringComparison.Ordinal));
        await stream.DisposeAsync();
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
