using System.Buffers.Binary;
using System.Text;
using IoTCom.Net.Debug;
using IoTCom.Net.Transport.Usb;

namespace IoTCom.Net.Tests.Transport;

public sealed class FirmwareTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("firmware.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_conformance_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var data = v.Hex("hex");
        var kind = v.GetProperty("kind").GetString()!;
        string got;
        try
        {
            got = kind switch
            {
                "ihex" => IntelHex.Parse(Encoding.ASCII.GetString(data)).Describe(),
                "dfu" => DfuFile.Parse(data).Describe(),
                "layout" => DfuSeLayout.Parse(Encoding.ASCII.GetString(data))?.Describe() ?? "error",
                "rtt" => RttControlBlock.Parse(data, 0x20000400)?.Describe() ?? "error",
                _ => throw new ArgumentException(kind),
            };
        }
        catch (ProtocolException)
        {
            got = "error";
        }

        Assert.Equal(v.GetProperty("expected").GetString(), got);
    }

    [Fact]
    public void Crc32_check_value() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void Intel_hex_round_trips_across_64k_and_keeps_the_entry_point()
    {
        var a = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var image = new FirmwareImage([new FirmwareSegment(0x0800FFF0, a), new FirmwareSegment(0x08020000, [1, 2, 3])], 0x08000189);
        var text = IntelHex.Format(image);
        Assert.Contains(":020000040800F2", text);
        Assert.Contains(":020000040801F1", text);
        var back = IntelHex.Parse(text);
        Assert.Equal(image.Describe(), back.Describe());
        Assert.Equal(0x08000189u, back.EntryPoint);
    }

    [Theory]
    [InlineData(":10010000214601360121470136007EFE09D2190140\n", "end-of-file")]
    [InlineData(":10010000214601360121470136007EFE09D2190141\n:00000001FF\n", "checksum")]
    [InlineData(":0201000021\n:00000001FF\n", "length")]
    [InlineData(":00000001FF\n:0100000000FF\n", "after the end")]
    public void Bad_hex_is_rejected(string text, string reason) => Assert.Contains(reason, Assert.Throws<ProtocolException>(() => IntelHex.Parse(text)).Message);

    [Fact]
    public void Dfu_suffix_and_dfuse_round_trip()
    {
        var plain = DfuFile.AddSuffix("firmware"u8, 0x0483, 0xDF11);
        var p = DfuFile.Parse(plain);
        Assert.False(p.IsDfuSe);
        Assert.Equal(0x0483, p.VendorId);
        Assert.Equal("firmware"u8.ToArray(), p.Image.Segments[0].Data);
        plain[2] ^= 1;
        Assert.Contains("CRC", Assert.Throws<ProtocolException>(() => DfuFile.Parse(plain)).Message);

        var image = VirtualMcu.BuildDemoFirmware("2.0.0", 5000);
        var dfuse = DfuFile.Parse(DfuFile.BuildDfuSe(image, 0x0483, 0xDF11));
        Assert.True(dfuse.IsDfuSe);
        Assert.Equal("Internal Flash", dfuse.Targets[0].Name);
        Assert.Equal(new FirmwareImage(image.Segments).Describe(), dfuse.Image.Describe());   // DfuSe files carry no entry point
        Assert.Equal("2.0.0", dfuse.Image.FindTag("FWVER:"));
    }

    [Fact]
    public void Elf_load_segments_are_used()
    {
        var elf = new byte[52 + 32 + 8];
        new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }.CopyTo(elf, 0);
        (elf[4], elf[5]) = (1, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(24), 0x08000101);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(28), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(42), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(44), 1);
        var ph = elf.AsSpan(52);
        BinaryPrimitives.WriteUInt32LittleEndian(ph, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[4..], 84);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[8..], 0x20000000);   // VMA in RAM, LMA in flash
        BinaryPrimitives.WriteUInt32LittleEndian(ph[12..], 0x08000000);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[16..], 8);
        var image = Elf.Parse(elf);
        Assert.Equal(0x08000000u, image.Start);
        Assert.Equal(8, image.Size);
        Assert.Equal(0x08000101u, image.EntryPoint);
    }

    [Fact]
    public void Dfuse_layouts_parse()
    {
        var l = DfuSeLayout.Parse("@Internal Flash  /0x08000000/04*016Kg,01*064Kg,03*128Kg")!;
        Assert.Equal("Internal Flash: 08000000+4x16384rew,08010000+1x65536rew,08020000+3x131072rew", l.Describe());
        Assert.Equal(0x08020000u, l.Find(0x08030000)!.Value.Start);
        Assert.Null(DfuSeLayout.Parse("Not a layout"));
        Assert.Equal("Option Bytes: 1FFFC000+1x16rw", DfuSeLayout.Parse("@Option Bytes  /0x1FFFC000/01*016 e")!.Describe());
    }
}

public sealed class DebugProbeTests
{
    private static (VirtualMcu Mcu, DebugProbe Probe) Rig(bool writes = false, string version = "1.0.0")
    {
        var mcu = new VirtualMcu(firmware: VirtualMcu.BuildDemoFirmware(version));
        var probe = DebugProbe.Create(o =>
        {
            o.UseVirtual(new VirtualProbeBackend(mcu)).UseTarget("STM32F411CEUx");
            if (writes) o.AllowWrites();
        });
        return (mcu, probe);
    }

    [Fact]
    public async Task Reads_memory_and_rtt_logs_read_only()
    {
        var (mcu, probe) = Rig();
        await using var _ = probe;
        mcu.Step(2.5);
        Assert.Equal(0x10006431u, await probe.ReadUInt32Async(0xE0042000));   // DBGMCU_IDCODE
        Assert.InRange(await probe.ReadUInt32Async(VirtualMcu.UptimeAddress), 2400u, 2600u);
        var rtt = await probe.AttachRttAsync(VirtualMcu.RamBase, VirtualMcu.RamSize);
        Assert.Equal(VirtualMcu.RttAddress, rtt.Address);
        Assert.Equal("Terminal", Assert.Single(rtt.UpChannels).Name);
        var log = await rtt.ReadTextAsync();
        Assert.Contains("IoTCom demo app 1.0.0 booted on STM32F411CEUx", log);
        Assert.Contains("counter=3", log);   // the firmware logs at boot (t=0), then every second
        Assert.Equal("", await rtt.ReadTextAsync());   // read offset advanced
        mcu.Step(1);
        Assert.Contains("counter=4", await rtt.ReadTextAsync());

        var (state, pc) = await probe.CoreAsync();
        Assert.NotEqual(CoreState.LockedUp, state);
        Assert.InRange(pc, VirtualMcu.FlashBase, VirtualMcu.FlashBase + 0x1000);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => probe.FlashAsync(VirtualMcu.BuildDemoFirmware("9.9.9")));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => probe.HaltAsync());
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => probe.WriteMemoryAsync(VirtualMcu.RamBase, new byte[4]));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => rtt.WriteAsync("led on\n"u8.ToArray()));
        await Assert.ThrowsAsync<DeviceException>(() => probe.ReadMemoryAsync(0x40000000, 4));
    }

    [Fact]
    public async Task Flashing_boots_the_new_firmware()
    {
        var (mcu, probe) = Rig(writes: true);
        await using var _ = probe;
        var phases = new List<string>();
        await probe.FlashAsync(VirtualMcu.BuildDemoFirmware("1.1.0"), new SyncProgress(p => phases.Add(p.Phase)));
        Assert.Equal(["Erasing", "Programming", "Verifying"], phases.Distinct());
        Assert.Equal("1.1.0", mcu.FirmwareVersion);
        mcu.Step(0.5);
        var rtt = await probe.AttachRttAsync(VirtualMcu.RamBase, VirtualMcu.RamSize);
        Assert.Contains("app 1.1.0 booted", await rtt.ReadTextAsync());

        Assert.Equal(7, await rtt.WriteAsync("led on\n"u8.ToArray()));
        mcu.Step(0.1);
        Assert.Contains("LED on", await rtt.ReadTextAsync());
        Assert.True(mcu.Led);

        await probe.HaltAsync();
        mcu.Step(5);
        Assert.Equal("", await rtt.ReadTextAsync());   // halted: no logs
        Assert.Equal(CoreState.Halted, (await probe.CoreAsync()).State);
        await probe.RunAsync();

        await probe.EraseAllAsync();
        await probe.ResetAsync();
        Assert.Equal(CoreState.LockedUp, (await probe.CoreAsync()).State);
        Assert.Null(mcu.FirmwareVersion);
        await Assert.ThrowsAsync<DeviceException>(() => probe.AttachRttAsync(VirtualMcu.RamBase, VirtualMcu.RamSize));
        await Assert.ThrowsAsync<DeviceException>(() => probe.FlashAsync(FirmwareImage.FromBinary(new byte[16], 0x00000000)));
    }

    [Fact]
    public async Task Wrong_target_and_rtt_ring_wrap()
    {
        var mcu = new VirtualMcu();
        await using var wrong = DebugProbe.Create(o => o.UseVirtual(new VirtualProbeBackend(mcu)).UseTarget("nRF52840_xxAA"));
        Assert.Contains("STM32F411CEUx", (await Assert.ThrowsAsync<DeviceException>(() => wrong.ConnectAsync().AsTask())).Message);
        Assert.Throws<ArgumentException>(() => DebugProbe.Create(_ => { }));

        Assert.Equal(10u, RttRing.Available(5, 1019, 1024));
        Assert.Equal([(1019u, 5u), (0u, 5u)], RttRing.ReadSpans(5, 1019, 1024));
        Assert.Equal(1023u, RttRing.Free(7, 7, 1024));
    }

    private sealed class SyncProgress(Action<FlashProgress> report) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => report(value);
    }
}

public sealed class DfuTests
{
    private static (VirtualMcu Mcu, VirtualUsbBus Bus, VirtualDfuDevice Device) Rig()
    {
        var mcu = new VirtualMcu(firmware: VirtualMcu.BuildDemoFirmware("1.0.0"));
        var bus = new VirtualUsbBus();
        var dfu = bus.Add(new VirtualDfuDevice(mcu));
        return (mcu, bus, dfu);
    }

    [Fact]
    public async Task Dfuse_download_and_leave_boot_the_new_firmware()
    {
        var (mcu, bus, device) = Rig();
        await using var dfu = DfuClient.Create(o => o.UseVirtual(bus).AllowWrites());
        await dfu.ConnectAsync();
        Assert.Equal("Internal Flash: 08000000+128x2048rew", dfu.Layout!.Describe());
        Assert.Equal(DfuState.DfuIdle, (await dfu.GetStatusAsync()).State);
        var image = DfuFile.Parse(DfuFile.BuildDfuSe(VirtualMcu.BuildDemoFirmware("2.0.0", 9000), 0x0483, 0xDF11)).Image;
        var phases = new List<string>();
        await dfu.DownloadAsync(image, new Progress(p => phases.Add(p.Phase)));
        Assert.Equal(5, device.PagesErased);   // 9000 bytes = 5 pages of 2 KiB
        Assert.Equal(5, device.BlocksWritten);
        Assert.Equal(["Erasing", "Programming"], phases.Distinct());
        var back = await dfu.UploadAsync(9000, VirtualMcu.FlashBase);
        Assert.Equal(image.Segments[0].Data, back);
        await dfu.LeaveAsync();
        Assert.True(device.Left);
        Assert.Equal("2.0.0", mcu.FirmwareVersion);
    }

    [Fact]
    public async Task Read_only_uploads_and_refuses_downloads()
    {
        var (_, bus, _) = Rig();
        await using var dfu = DfuClient.Create(o => o.UseVirtual(bus));
        var head = await dfu.UploadAsync(16, VirtualMcu.FlashBase);
        Assert.Equal(VirtualMcu.RamBase + VirtualMcu.RamSize, BinaryPrimitives.ReadUInt32LittleEndian(head));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => dfu.DownloadAsync(VirtualMcu.BuildDemoFirmware("6.6.6")));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => dfu.LeaveAsync());
        await using var none = DfuClient.Create(o => o.UseVirtual(new VirtualUsbBus()));
        Assert.Contains("DFU mode", (await Assert.ThrowsAsync<DeviceException>(() => none.ConnectAsync())).Message);
    }

    [Fact]
    public async Task Out_of_range_images_are_refused_before_erasing()
    {
        var (mcu, bus, device) = Rig();
        await using var dfu = DfuClient.Create(o => o.UseVirtual(bus).AllowWrites());
        await Assert.ThrowsAsync<DeviceException>(() => dfu.DownloadAsync(FirmwareImage.FromBinary(new byte[64], 0x08040000)));
        Assert.Equal(0, device.PagesErased);
        Assert.Equal("1.0.0", mcu.FirmwareVersion);
        Assert.Equal("errPROG", new DfuStatus(6, TimeSpan.Zero, DfuState.Error).StatusText);
        Assert.Equal(new DfuStatus(0, TimeSpan.FromMilliseconds(300), DfuState.DownloadBusy), DfuStatus.Parse(new DfuStatus(0, TimeSpan.FromMilliseconds(300), DfuState.DownloadBusy).Encode()));
    }

    private sealed class Progress(Action<FlashProgress> report) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => report(value);
    }
}
