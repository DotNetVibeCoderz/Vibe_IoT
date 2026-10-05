using IoTCom.Net.Protocols.IsoTp;
using IoTCom.Net.Protocols.Uds;
using IoTCom.Net.Transport.Can;

namespace IoTCom.Net.Tests.Automotive;

/// <summary>Runs only when the Rust iotcom_isotp library is available.</summary>
public sealed class IsoTpFactAttribute : FactAttribute
{
    public IsoTpFactAttribute()
    {
        if (!IsoTpMachine.IsSupported) Skip = "iotcom_isotp native library not built (run: cargo build --release in rust/).";
    }
}

public sealed class IsoTpChannelTests
{
    [IsoTpFact]
    public async Task Long_message_with_flow_control_round_trips()
    {
        var net = new VirtualCanNetwork();
        await using var a = IsoTpChannel.Create(net.CreateNode(), o => { o.TxId = 0x700; o.RxId = 0x708; });
        await using var b = IsoTpChannel.Create(net.CreateNode(), o => { o.TxId = 0x708; o.RxId = 0x700; o.BlockSize = 8; o.SeparationTime = 0; });
        await a.ConnectAsync();
        await b.ConnectAsync();
        var payload = Enumerable.Range(0, 1500).Select(i => (byte)i).ToArray();
        await a.SendAsync(payload);
        Assert.Equal(payload, await b.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        await b.SendAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await a.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [IsoTpFact]
    public async Task Can_fd_channel_carries_large_frames()
    {
        var net = new VirtualCanNetwork();
        await using var a = IsoTpChannel.Create(net.CreateNode(o => o.Fd = true), o => { o.TxId = 0x18DA10F1; o.RxId = 0x18DAF110; o.Fd = true; o.MaxMessageLength = 10_000; });
        await using var b = IsoTpChannel.Create(net.CreateNode(o => o.Fd = true), o => { o.TxId = 0x18DAF110; o.RxId = 0x18DA10F1; o.Fd = true; o.MaxMessageLength = 10_000; });
        await a.ConnectAsync();
        await b.ConnectAsync();
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i * 3)).ToArray();
        await a.SendAsync(payload);
        Assert.Equal(payload, await b.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [IsoTpFact]
    public async Task Missing_receiver_times_out_and_overflow_is_reported()
    {
        var net = new VirtualCanNetwork();
        await using var a = IsoTpChannel.Create(net.CreateNode(), o => { o.TxId = 0x700; o.RxId = 0x708; o.FlowControlTimeout = TimeSpan.FromMilliseconds(100); });
        await a.ConnectAsync();
        var ex = await Assert.ThrowsAsync<IsoTpException>(() => a.SendAsync(new byte[50]));
        Assert.Equal(IsoTpError.TimeoutBs, ex.Error);

        await using var small = IsoTpChannel.Create(net.CreateNode(), o => { o.TxId = 0x708; o.RxId = 0x700; o.MaxMessageLength = 20; });
        await small.ConnectAsync();
        ex = await Assert.ThrowsAsync<IsoTpException>(() => a.SendAsync(new byte[50]));
        Assert.Equal(IsoTpError.Overflow, ex.Error);
    }
}

public sealed class UdsTests
{
    private static async Task<(EcuSimulator Ecu, UdsClient Uds, ObdClient Obd)> RigAsync(bool readOnly = false)
    {
        var net = new VirtualCanNetwork();
        var ecu = EcuSimulator.Create(net.CreateNode());
        await ecu.StartAsync();
        var uds = UdsClient.Create(net.CreateNode(), o => o.ReadOnly = readOnly);
        await uds.ConnectAsync();
        var obd = ObdClient.Create(net.CreateNode(), o => o.ReadOnly = readOnly);
        await obd.ConnectAsync();
        return (ecu, uds, obd);
    }

    [Fact]
    public void Dtc_codes_use_sae_notation()
    {
        Assert.Equal("P0301", Dtc.Parse("P0301").ToString());
        Assert.Equal("U0100-87", Dtc.Parse("U0100-87").ToString());
        Assert.Equal(0x0301u << 8, Dtc.Parse("P0301").Code);
        Assert.Equal("C1234", Dtc.FromObd(0x5234).ToString());
        Assert.Equal("B0001", Dtc.FromObd(0x8001).ToString());
    }

    [Fact]
    public void Anatomy_describes_requests_and_negative_responses()
    {
        var f = UdsAnatomy.Describe(new byte[] { 0x22, 0xF1, 0x90 });
        Assert.Equal(["SID", "DID"], f.Select(x => x.Name));
        var n = UdsAnatomy.Describe(new byte[] { 0x7F, 0x27, 0x35 });
        Assert.Equal("invalid key", n[2].Value);
    }

    [IsoTpFact]
    public async Task Reads_identification_and_dtcs()
    {
        var (ecu, uds, obd) = await RigAsync();
        await using (ecu)
        await using (uds)
        await using (obd)
        {
            Assert.Equal("1HGCM82633A004352", await uds.ReadVinAsync());
            Assert.Equal("1.4.2", await uds.ReadStringAsync(UdsDid.SoftwareVersion));
            var dtcs = await uds.ReadDtcsAsync();
            Assert.Contains(dtcs, d => d.ToString() == "P0301" && d.Status.HasFlag(DtcStatus.Confirmed));
            Assert.Contains(dtcs, d => d.ToString() == "P0420" && d.Status.HasFlag(DtcStatus.Pending));
            var confirmed = await uds.ReadDtcsAsync(DtcStatus.Confirmed);
            Assert.Single(confirmed);
            Assert.Null(await uds.RequestAsync(new byte[] { 0x3E, 0x80 })); // suppressed positive response
            Assert.Equal(new byte[] { 0x7E, 0x00 }, await uds.RequestAsync(new byte[] { 0x3E, 0x00 }));
            var nrc = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => uds.RequestAsync(new byte[] { 0x23, 0x00 }));
            Assert.Equal(UdsNrc.ServiceNotSupported, nrc.ResponseCode);
        }
    }

    [IsoTpFact]
    public async Task Security_access_guards_writes()
    {
        var (ecu, uds, obd) = await RigAsync();
        await using (ecu)
        await using (uds)
        await using (obd)
        {
            var e = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => uds.WriteDataByIdentifierAsync(UdsDid.RepairShopCode, "X"u8.ToArray()));
            Assert.Equal(UdsNrc.ServiceNotSupportedInActiveSession, e.ResponseCode);

            var (p2, p2x) = await uds.StartSessionAsync(UdsSession.Extended);
            Assert.Equal(TimeSpan.FromMilliseconds(50), p2);
            Assert.Equal(TimeSpan.FromSeconds(5), p2x);
            e = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => uds.WriteDataByIdentifierAsync(UdsDid.RepairShopCode, "X"u8.ToArray()));
            Assert.Equal(UdsNrc.SecurityAccessDenied, e.ResponseCode);

            e = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => uds.SecurityAccessAsync(0x01, seed => new byte[seed.Length]));
            Assert.Equal(UdsNrc.InvalidKey, e.ResponseCode);
            Assert.True(await uds.SecurityAccessAsync(0x01, EcuSimulator.ComputeKey));
            Assert.True(ecu.SecurityUnlocked);

            await uds.WriteDataByIdentifierAsync(UdsDid.RepairShopCode, "GRAVICODE-01"u8.ToArray());
            Assert.Equal("GRAVICODE-01", await uds.ReadStringAsync(UdsDid.RepairShopCode));

            // Routine 0x0203 answers "response pending" (0x78) before the result.
            Assert.Equal(new byte[] { 0x00 }, await uds.RoutineControlAsync(0x01, 0x0203));

            await uds.ClearDtcsAsync();
            Assert.Empty(await uds.ReadDtcsAsync());
        }
    }

    [IsoTpFact]
    public async Task Read_only_mode_blocks_writes_locally()
    {
        var (ecu, uds, obd) = await RigAsync(readOnly: true);
        await using (ecu)
        await using (uds)
        await using (obd)
        {
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => uds.ClearDtcsAsync());
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => uds.EcuResetAsync());
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => obd.ClearDtcsAsync());
            Assert.NotEmpty(ecu.Dtcs);
        }
    }

    [IsoTpFact]
    public async Task Obd_scan_tool_reads_live_data_vin_and_dtcs()
    {
        var (ecu, uds, obd) = await RigAsync();
        await using (ecu)
        await using (uds)
        await using (obd)
        {
            var supported = await obd.GetSupportedPidsAsync();
            Assert.Contains((byte)0x0C, supported);
            Assert.Contains((byte)0x42, supported);
            var rpm = await obd.ReadPidAsync(ObdPids.EngineRpm);
            Assert.InRange(rpm.Value, 600, 6000);
            var coolant = await obd.ReadPidAsync(ObdPids.CoolantTemperature);
            Assert.InRange(coolant.Value, -40, 130);
            Assert.Equal("1HGCM82633A004352", await obd.ReadVinAsync()); // multi-frame answer to a functional request
            Assert.Equal(["P0301"], (await obd.ReadDtcsAsync()).Select(d => d.ToString()));
            Assert.Equal(["P0420"], (await obd.ReadPendingDtcsAsync()).Select(d => d.ToString()));
            await obd.ClearDtcsAsync();
            Assert.Empty(await obd.ReadDtcsAsync());
        }
    }

    [Fact]
    public void Pid_encoding_round_trips()
    {
        foreach (var pid in ObdPids.All.Values)
        {
            var mid = (pid.Min + pid.Max) / 2;
            Assert.Equal(mid, pid.Decode(pid.Encode(mid)), 0);
        }
        Assert.Equal(new byte[] { 0x1A, 0xF8 }, ObdPids.EngineRpm.Encode(1726));
    }
}
