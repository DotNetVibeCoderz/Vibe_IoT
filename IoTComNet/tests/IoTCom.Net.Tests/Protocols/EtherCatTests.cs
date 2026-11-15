using System.Buffers.Binary;
using System.Globalization;
using IoTCom.Net.Hosting;
using IoTCom.Net.Protocols.EtherCat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IoTCom.Net.Tests.Protocols;

public sealed class EtherCatCodecTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("ethercat.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_conformance_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var data = v.Hex("hex");
        var kind = v.GetProperty("kind").GetString()!;
        Assert.Equal(v.GetProperty("expected").GetString(), Describe(kind, data));
        if (kind == "payload" && EcatFrame.TryDecodePayload(data, out var datagrams, out _)) Assert.Equal(data, EcatFrame.EncodePayload(datagrams));
    }

    private static string Datagrams(IReadOnlyList<EcatDatagram> ds) => string.Join(';', ds.Select(d =>
        string.Create(CultureInfo.InvariantCulture, $"{d.Command.ToString().ToUpperInvariant()} idx={d.Index} addr=0x{d.Address:X8} len={d.Data.Length} irq=0x{d.Irq:X4} wkc={d.WorkingCounter} c={(d.Circulating ? 1 : 0)} data={Convert.ToHexString(d.Data)}")));

    private static string Describe(string kind, byte[] data)
    {
        switch (kind)
        {
            case "payload":
                return EcatFrame.TryDecodePayload(data, out var p, out _) ? Datagrams(p) : "error";
            case "ethernet":
                return EcatFrame.TryDecode(data, out var e, out _) ? $"returned={(EcatFrame.IsReturned(data) ? 1 : 0)} {Datagrams(e)}" : "error";
            case "mailbox":
                return EcatMailbox.TryDecode(data, out var m, out _)
                    ? string.Create(CultureInfo.InvariantCulture, $"type={(int)m!.Type} counter={m.Counter} address=0x{m.Address:X4} priority={m.Priority} payload={Convert.ToHexString(m.Payload)}")
                    : "error";
            case "sdo":
                if (!CoeSdo.TryDecode(data, out var sdo, out _)) return "error";
                var text = string.Create(CultureInfo.InvariantCulture, $"service={(int)sdo!.Service} command=0x{sdo.Command:X2} index=0x{sdo.Index:X4} sub={sdo.SubIndex} data={Convert.ToHexString(sdo.Data)}");
                return sdo.IsAbort ? text + $" abort=0x{sdo.AbortCode:X8}" : text;
            case "sii":
                try
                {
                    var i = EcatSii.Parse(data);
                    var sms = string.Join(',', i.SyncManagers.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Start:X4}:{x.Length}:{x.Control:X2}:{x.Type}")));
                    return string.Create(CultureInfo.InvariantCulture,
                        $"vendor=0x{i.Vendor:X8} product=0x{i.Product:X8} revision=0x{i.Revision:X8} serial={i.Serial} name={i.Name} mailbox={i.Mailbox.RxOffset:X4}:{i.Mailbox.RxSize},{i.Mailbox.TxOffset:X4}:{i.Mailbox.TxSize},{i.Mailbox.Protocols:X4} sms={sms} out={i.OutputBits} in={i.InputBits}");
                }
                catch (ProtocolException)
                {
                    return "error";
                }

            default:
                throw new ArgumentException(kind);
        }
    }

    [Fact]
    public void Frames_round_trip_with_padding_and_more_flags()
    {
        var frame = EcatFrame.Encode([
            EcatDatagram.Broadcast(EcatCommand.Brd, 0x0000, new byte[1], index: 7),
            EcatDatagram.Configured(EcatCommand.Fprd, 0x1001, 0x0130, new byte[2], index: 8),
        ]);
        Assert.Equal(60, frame.Length);   // padded to the Ethernet minimum
        Assert.Equal(0x88A4, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12)));
        Assert.False(EcatFrame.IsReturned(frame));
        Assert.True(EcatFrame.TryDecode(frame, out var datagrams, out var error), error);
        Assert.Equal(2, datagrams.Count);
        Assert.Equal(EcatCommand.Brd, datagrams[0].Command);
        Assert.Equal(7, datagrams[0].Index);
        Assert.Equal(0x1001, datagrams[1].Adp);
        Assert.Equal(0x0130, datagrams[1].Ado);
        Assert.Equal("FPRD 0x1001:0x0130 [2] wkc=0", datagrams[1].ToString());
        Assert.Equal("APRD #2:0x0000 [1] wkc=0", EcatDatagram.Positional(EcatCommand.Aprd, 2, 0, new byte[1]).ToString());
        Assert.Contains(EcatFrame.DescribeFields(frame), f => f.Name == "WKC");
        Assert.Contains(EcatFrame.DescribeFields(frame), f => f.Name == "Padding");
    }

    [Theory]
    [InlineData("FFFFFFFFFFFF010101010101", "14 bytes")]                                    // too short
    [InlineData("FFFFFFFFFFFF0101010101010800", "EtherType")]
    [InlineData("FFFFFFFFFFFF01010101010188A40C20", "type 2")]                              // type 2
    [InlineData("FFFFFFFFFFFF01010101010188A40D10070000000000010000000000", "announces")] // length past the end
    [InlineData("FFFFFFFFFFFF01010101010188A40D101F00000000000100000000000000", "Unknown command")]
    public void Malformed_frames_are_rejected(string hex, string reason)
    {
        Assert.False(EcatFrame.TryDecode(Convert.FromHexString(hex), out _, out var error));
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Mailbox_and_sdo_round_trip()
    {
        var request = CoeSdo.DownloadRequest(0x8000, 0x15, [2, 0]);
        var mbx = new EcatMailbox(EcatMailboxType.CoE, 3, request).Encode(128);
        Assert.Equal(128, mbx.Length);
        Assert.True(EcatMailbox.TryDecode(mbx, out var back, out _));
        Assert.Equal(EcatMailboxType.CoE, back!.Type);
        Assert.Equal(3, back.Counter);
        Assert.True(CoeSdo.TryDecode(back.Payload, out var sdo, out _));
        Assert.True(sdo!.IsDownloadRequest);
        Assert.Equal(0x2B, sdo.Command);   // expedited, 2 bytes
        Assert.Equal([2, 0], sdo.Data);

        Assert.True(CoeSdo.TryDecode(CoeSdo.UploadResponse(0x1008, 0, "Sim DI8 24V"u8), out var name, out _));
        Assert.Equal("Sim DI8 24V"u8.ToArray(), name!.Data);   // normal transfer
        Assert.True(CoeSdo.TryDecode(CoeSdo.Abort(0x1000, 0, 0x06020000), out var abort, out _));
        Assert.True(abort!.IsAbort);
        Assert.Equal("object does not exist", CoeSdo.AbortText(abort.AbortCode));
    }

    [Fact]
    public void Sii_images_parse()
    {
        var sii = EcatSii.Build(0xE5C, 4, 0x10000, 1004, (0x1000, 128, 0x1080, 128, 4), [
            (EcatSii.Strings, EcatSii.StringsCategory("Sim AI4")),
            (EcatSii.General, EcatSii.GeneralCategory(1, coe: true)),
            (EcatSii.SyncManager, EcatSii.SyncManagerCategory((0x1000, 128, 0x26, 1), (0x1080, 128, 0x22, 2), (0x1180, 8, 0x20, 4))),
            (EcatSii.TxPdo, EcatSii.PdoCategory(0x1A00, 3, (0x6000, 0x11, 16), (0x6010, 0x11, 16), (0x6020, 0x11, 16), (0x6030, 0x11, 16))),
        ]);
        var info = EcatSii.Parse(sii);
        Assert.Equal("Sim AI4", info.Name);
        Assert.Equal(1004u, info.Serial);
        Assert.Equal(64, info.InputBits);
        Assert.Equal(0, info.OutputBits);
        Assert.Equal(3, info.SyncManagers.Count);
        Assert.Equal((ushort)128, info.Mailbox.TxSize);
    }
}

public sealed class EtherCatMasterTests
{
    private static async Task<(EthercatRackSimulator Rack, EthercatMaster Master)> RigAsync(bool writes = false)
    {
        var rack = new EthercatRackSimulator();
        var master = EthercatMaster.Create(o =>
        {
            o.UseLink(rack.Segment);
            o.FrameTimeout = TimeSpan.FromMilliseconds(30);
            if (writes) o.AllowWrites();
        });
        await master.ScanAsync();
        return (rack, master);
    }

    [Fact]
    public async Task Scans_the_line()
    {
        var (_, master) = await RigAsync();
        await using var _m = master;
        Assert.Equal(["Sim EK coupler", "Sim DI8 24V", "Sim DO8 24V", "Sim AI4 10V"], master.Slaves.Select(s => s.Name));
        Assert.Equal([0x1001, 0x1002, 0x1003, 0x1004], master.Slaves.Select(s => (int)s.Station));
        Assert.All(master.Slaves, s => Assert.Equal(EcatState.Init, s.State));
        Assert.Equal([0, 1, 0, 8], master.Slaves.Select(s => s.InputBytes));
        Assert.Equal([0, 0, 1, 0], master.Slaves.Select(s => s.OutputBytes));
        Assert.False(master.Slaves[0].HasCoe);
        Assert.Equal(1004u, master.Slaves[3].Serial);
        Assert.Contains("vendor 0x00000E5C", master.Slaves[1].ToString());
    }

    [Fact]
    public async Task Read_only_master_reaches_safe_op_and_reads_inputs()
    {
        var (rack, master) = await RigAsync();
        await using var _m = master;
        await master.RequestStateAsync(EcatState.SafeOp);
        Assert.All(master.Slaves, s => Assert.Equal(EcatState.SafeOp, s.State));
        rack.Step(0.1);
        Assert.Equal(2, await master.ExchangeAsync());   // LRD: DI8 + AI4
        Assert.Equal(2, master.Statistics.ExpectedWorkingCounter);
        var di = master.InputsOf(master.Slaves[1])[0];
        Assert.Equal(0x0C, di & 0x0C);   // e-stop OK and guard closed
        var pressure = BinaryPrimitives.ReadInt16LittleEndian(master.InputsOf(master.Slaves[3])[4..]);
        Assert.InRange(pressure, 600, 620);

        Assert.Throws<ReadOnlyModeException>(() => master.SetOutputBit(master.Slaves[2], 0, true));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => master.RequestStateAsync(EcatState.Op));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => master.SdoWriteAsync(master.Slaves[3], 0x8000, 0x15, [1, 0]));
        Assert.Equal(EcatState.SafeOp, rack.DigitalOut.State);
    }

    [Fact]
    public async Task Op_drives_the_conveyor()
    {
        var (rack, master) = await RigAsync(writes: true);
        await using var _m = master;
        await master.RequestStateAsync(EcatState.Op);
        Assert.All(master.Slaves, s => Assert.Equal(EcatState.Op, s.State));
        Assert.Equal(4, master.Statistics.ExpectedWorkingCounter);   // LRW: DO8 write (2) + DI8 read (1) + AI4 read (1)
        master.SetOutputBit(master.Slaves[2], 0, true);   // motor
        master.SetOutputBit(master.Slaves[2], 2, true);   // green lamp
        await master.ExchangeAsync();
        Assert.True(rack.MotorCommanded);
        for (var i = 0; i < 40; i++)
        {
            rack.Step(0.1);
            await master.ExchangeAsync();
        }

        Assert.Equal(0, master.Statistics.WorkingCounterErrors);
        Assert.True((master.InputsOf(master.Slaves[1])[0] & 1) != 0, "motor feedback");
        var speed = BinaryPrimitives.ReadInt16LittleEndian(master.InputsOf(master.Slaves[3])[6..]) / 100.0;
        Assert.InRange(speed, 15, 19);
        Assert.True(rack.Parts > 0);

        rack.PressEmergencyStop();
        for (var i = 0; i < 30; i++)
        {
            rack.Step(0.1);
            await master.ExchangeAsync();
        }

        Assert.Equal(0, master.InputsOf(master.Slaves[1])[0] & 0x05);   // stopped, e-stop pressed
    }

    [Fact]
    public async Task Coe_objects_read_write_and_abort()
    {
        var (rack, master) = await RigAsync(writes: true);
        await using var _m = master;
        var ai = master.Slaves[3];
        await Assert.ThrowsAsync<EthercatException>(() => master.SdoReadAsync(ai, 0x1008, 0));   // INIT: no mailbox
        await master.RequestStateAsync(EcatState.PreOp);
        Assert.Equal("Sim AI4 10V"u8.ToArray().Length, (await master.SdoReadAsync(ai, 0x1008, 0)).Length);
        Assert.Equal(1004u, BinaryPrimitives.ReadUInt32LittleEndian(await master.SdoReadAsync(ai, 0x1018, 4)));
        await master.SdoWriteAsync(ai, 0x8000, 0x15, [2, 0]);
        Assert.Equal([2, 0], rack.Analog.GetObject(0x8000, 0x15));
        var range = await Assert.ThrowsAsync<EthercatException>(() => master.SdoWriteAsync(ai, 0x8000, 0x15, [9, 0]));
        Assert.Equal(0x06090030u, range.AbortCode);
        var readOnly = await Assert.ThrowsAsync<EthercatException>(() => master.SdoWriteAsync(ai, 0x1008, 0, [1]));
        Assert.Contains("read-only object", readOnly.Message);
        var missing = await Assert.ThrowsAsync<EthercatException>(() => master.SdoReadAsync(ai, 0x2000, 0));
        Assert.Equal(0x06020000u, missing.AbortCode);
        await Assert.ThrowsAsync<EthercatException>(() => master.SdoReadAsync(master.Slaves[0], 0x1000, 0));   // coupler: no CoE
    }

    [Fact]
    public async Task Cable_break_shows_in_the_working_counter()
    {
        var (rack, master) = await RigAsync(writes: true);
        await using var _m = master;
        await master.RequestStateAsync(EcatState.Op);
        Assert.Equal(4, await master.ExchangeAsync());
        rack.Segment.BreakAfter = 2;   // DO8 and AI4 unreachable
        Assert.Equal(1, await master.ExchangeAsync());
        Assert.Equal(1, master.Statistics.WorkingCounterErrors);
        rack.Segment.BreakAfter = null;
        Assert.Equal(4, await master.ExchangeAsync());
    }

    [Fact]
    public async Task Lost_frames_are_retried_and_counted()
    {
        var (rack, master) = await RigAsync();
        await using var _m = master;
        await master.RequestStateAsync(EcatState.SafeOp);
        rack.Segment.LossRate = 0.3;
        for (var i = 0; i < 50; i++) await master.ExchangeAsync();
        Assert.InRange(master.Statistics.LostFrames, 5, 30);
        await master.RefreshStatesAsync();   // acyclic frames retry and still succeed
        rack.Segment.LossRate = 1;
        await Assert.ThrowsAsync<IoTComTimeoutException>(() => master.RefreshStatesAsync());
    }

    [Fact]
    public async Task Slave_errors_report_the_al_status_code()
    {
        var (rack, master) = await RigAsync(writes: true);
        await using var _m = master;
        await master.RequestStateAsync(EcatState.Op);
        rack.DigitalOut.RaiseError(0x001B);   // sync manager watchdog: drops to SAFE-OP
        await master.RefreshStatesAsync();
        Assert.Equal(EcatState.SafeOp, master.Slaves[2].State);
        Assert.Equal(0x001B, master.Slaves[2].StatusCode);
        Assert.False(rack.MotorCommanded);   // outputs held safe below OP
        var refused = await Assert.ThrowsAsync<EthercatException>(() => master.RequestStateAsync(EcatState.Op));
        Assert.Contains("sync manager watchdog", refused.Message);
        await master.AcknowledgeErrorAsync(master.Slaves[2], EcatState.Op);
        Assert.Equal(EcatState.Op, master.Slaves[2].State);
    }

    [Fact]
    public async Task Traffic_is_tapped_and_empty_lines_scan_empty()
    {
        var (rack, master) = await RigAsync();
        var tap = new RecordingTap();
        master.AddTap(tap);
        await master.RefreshStatesAsync();
        await master.DisposeAsync();
        Assert.Contains(tap.Snapshot(), f => f.Protocol == "ethercat" && f.Direction == FrameDirection.Inbound);

        // The caller owns a link passed with UseLink: a second master can use the same segment.
        await using var second = EthercatMaster.Create(o => o.UseLink(rack.Segment));
        Assert.Equal(4, (await second.ScanAsync()).Count);

        await using var empty = EthercatMaster.Create(o => o.UseLink(new VirtualEthercatSegment()));
        Assert.Empty(await empty.ScanAsync());
        Assert.Throws<ArgumentException>(() => EthercatMaster.Create(_ => { }));
    }

    [Fact]
    public async Task Hosting_opens_the_link_and_closes_the_line_safely()
    {
        var rack = new EthercatRackSimulator();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddIoTCom(iot => iot.AddEthercatMaster("line", o => o.UseLink(rack.Segment).AllowWrites()));
        using var host = builder.Build();
        await host.StartAsync();
        var master = host.Services.GetRequiredKeyedService<EthercatMaster>("line");
        Assert.Equal(EndpointState.Connected, master.State);
        Assert.Equal("line", master.Name);
        await master.RequestStateAsync(EcatState.Op);
        Assert.Equal(EcatState.Op, rack.DigitalOut.State);
        await host.StopAsync();
        Assert.Equal(EcatState.PreOp, rack.DigitalOut.State);   // outputs safe when the host stops
    }
}
