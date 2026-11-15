// ProbeFlash — flash a microcontroller over SWD, stream its RTT log, and update it again over USB DFU.
//
//   dotnet run                                              # simulated STM32F411: SWD flash, RTT, then DfuSe update
//   dotnet run -- STM32F411CEUx firmware.elf                # a real board through the only probe (probe-rs)
//
// Flashing replaces the firmware: the real-board path asks before it writes.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Text;
using IoTCom.Net.Debug;
using IoTCom.Net.Transport.Usb;

if (args.Length >= 2)
{
    var image = FirmwareImage.Load(args[1]);
    Console.Write($"Flash {image} to {args[0]}? [y/N] ");
    if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y") return;
    await using var real = DebugProbe.Create(o => o.UseTarget(args[0]).AllowWrites());
    await real.FlashAsync(image, new Progress<FlashProgress>(p => Console.Write($"\r{p.Phase,-12} {p.Fraction,6:P0}")));
    Console.WriteLine($"\nFlashed and reset {real.Target} via {real.ProbeName}.");
    return;
}

// 1. SWD: flash firmware 1.1.0, let it boot, read its RTT log.
var mcu = new VirtualMcu(firmware: VirtualMcu.BuildDemoFirmware("1.0.0"));
await using var probe = DebugProbe.Create(o => o.UseVirtual(new VirtualProbeBackend(mcu)).UseTarget("STM32F411CEUx").AllowWrites());
await probe.FlashAsync(VirtualMcu.BuildDemoFirmware("1.1.0"), new Progress<FlashProgress>(p => { if (p.Fraction >= 1) Console.WriteLine($"  {p.Phase} done"); }));
mcu.Step(2.2);
var rtt = await probe.AttachRttAsync(VirtualMcu.RamBase, VirtualMcu.RamSize);
Console.WriteLine($"RTT at 0x{rtt.Address:X8}:");
Console.Write(await rtt.ReadTextAsync());
await rtt.WriteAsync(Encoding.ASCII.GetBytes("led on\n"));
mcu.Step(0.1);
Console.Write(await rtt.ReadTextAsync());
Console.WriteLine($"uptime counter at 0x{VirtualMcu.UptimeAddress:X8}: {await probe.ReadUInt32Async(VirtualMcu.UptimeAddress)} ms");

// 2. USB DFU: the same chip in its system bootloader (BOOT0 set), updated to 2.0.0 from a DfuSe file.
var bus = new VirtualUsbBus();
bus.Add(new VirtualDfuDevice(mcu));
var dfuFile = DfuFile.Parse(DfuFile.BuildDfuSe(VirtualMcu.BuildDemoFirmware("2.0.0"), 0x0483, 0xDF11));
await using var dfu = DfuClient.Create(o => o.UseVirtual(bus).AllowWrites());
await dfu.ConnectAsync();
Console.WriteLine($"DFU device {dfu.Device!.Id}: {dfu.Layout!.Describe()}");
await dfu.DownloadAsync(dfuFile.Image);
await dfu.LeaveAsync();
Console.WriteLine($"after DFU: firmware {mcu.FirmwareVersion}");
