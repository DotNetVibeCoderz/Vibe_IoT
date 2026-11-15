using System.ComponentModel;
using System.Globalization;
using System.Net;
using IoTCom.Net.Adapters.Bacnet;
using IoTCom.Net.Protocols.Bacnet;
using IoTCom.Net.Transports;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

/// <summary>Shared options: the local network (UDP 47808) or the in-process air handling unit (--sim).</summary>
internal class BacnetSettings : CommandSettings
{
    [CommandOption("--sim"), Description("Use an in-process BACnet/IP air handling unit (device 2604).")]
    public bool Sim { get; init; }

    [CommandOption("--broadcast"), Description("Broadcast address for Who-Is (default 255.255.255.255).")]
    public string? Broadcast { get; init; }

    [CommandOption("--address"), Description("Device address when broadcasts do not arrive (ip[:port]); requires the device instance argument.")]
    public string? Address { get; init; }

    [CommandOption("--frames"), Description("Print every BACnet/IP datagram.")]
    public bool Frames { get; init; }
}

internal sealed class BacnetSim : IAsyncDisposable
{
    public static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.186.192"), BacnetDeviceOptions.DefaultPort);
    public InMemoryDatagramNetwork Net { get; } = new();
    public BacnetAhuSimulator Ahu { get; }
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public BacnetSim() => Ahu = BacnetAhuSimulator.Create(o => { o.UseInMemory(Net, new IPEndPoint(IPAddress.Parse("10.60.0.2"), BacnetDeviceOptions.DefaultPort)); o.BroadcastAddress = Group; });

    public async Task StartAsync()
    {
        await Ahu.StartAsync();
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cts.Token);
                    Ahu.Step(10);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) await _loop;
        await Ahu.DisposeAsync();
        _cts.Dispose();
    }
}

internal static class BacnetUi
{
    public static async Task<(BacnetIpClient Client, IAsyncDisposable Owner)> OpenAsync(BacnetSettings s, bool writes, CancellationToken ct)
    {
        BacnetSim? sim = null;
        if (s.Sim)
        {
            sim = new BacnetSim();
            await sim.StartAsync();
        }

        var client = BacnetIpClient.Create(o =>
        {
            if (sim is not null)
            {
                o.UseInMemory(sim.Net, new IPEndPoint(IPAddress.Parse("10.60.0.20"), BacnetDeviceOptions.DefaultPort));
                o.BroadcastAddress = BacnetSim.Group;
            }
            else if (s.Broadcast is not null)
            {
                o.BroadcastAddress = new IPEndPoint(IPAddress.Parse(s.Broadcast), BacnetDeviceOptions.DefaultPort);
            }

            if (writes) o.AllowWrites();
        });
        if (s.Frames) client.AddTap(new Printer());
        await client.ConnectAsync(ct);
        return (client, new Owner(client, sim));
    }

    /// <summary>Makes sure the client knows the device (discovery or --address).</summary>
    public static async Task EnsureAsync(BacnetIpClient client, BacnetSettings s, uint device, CancellationToken ct)
    {
        if (s.Address is { } a)
        {
            var parts = a.Split(':');
            client.AddDevice(device, new IPEndPoint(IPAddress.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : BacnetDeviceOptions.DefaultPort));
            return;
        }

        await client.DiscoverAsync(TimeSpan.FromSeconds(s.Sim ? 0.3 : 2), device, device, ct);
        if (!client.Devices.ContainsKey(device)) throw new InvalidOperationException($"Device {device} did not answer Who-Is. Try --address <ip> or check --broadcast.");
    }

    private sealed class Owner(BacnetIpClient client, BacnetSim? sim) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            if (sim is not null) await sim.DisposeAsync();
        }
    }

    private sealed class Printer : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) =>
            AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{frame.Timestamp.ToLocalTime():HH:mm:ss.fff} {(frame.Direction == FrameDirection.Inbound ? "←" : "→")} {Markup.Escape(frame.Summary ?? "")}[/]");
    }

    public static string Format(IReadOnlyList<BacnetValue> values) => values.Count == 1 ? values[0].ToString() : "[" + string.Join(", ", values) + "]";
}

internal sealed class BacnetDiscoverCommand : AsyncCommand<BacnetDiscoverCommand.Settings>
{
    public sealed class Settings : BacnetSettings
    {
        [CommandOption("-t|--timeout"), Description("Seconds to collect I-Am answers (default 3).")]
        public int Timeout { get; init; } = 3;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner) = await BacnetUi.OpenAsync(s, writes: false, ct);
        await using (owner)
        {
            var found = await client.DiscoverAsync(TimeSpan.FromSeconds(s.Sim ? 0.5 : s.Timeout), ct: ct);
            if (found.Count == 0)
            {
                Ui.Warn("No BACnet device answered Who-Is. Broadcasts must reach UDP 47808 (try --broadcast with your subnet's broadcast address).");
                return 1;
            }

            var table = new Table().Border(TableBorder.Rounded).AddColumns("Device", "Name", "Address", "Vendor", "Max APDU", "Segmentation");
            foreach (var d in found)
            {
                string name;
                try
                {
                    name = (await client.ReadPropertyAsync(d.Instance, new BacnetObjectId(BacnetObjectType.Device, d.Instance), BacnetPropertyId.ObjectName, ct: ct))[0].ToString();
                }
                catch (IoTComException)
                {
                    name = "?";
                }

                table.AddRow(d.Instance.ToString(CultureInfo.InvariantCulture), Markup.Escape(name), d.Address.ToString(), d.VendorId.ToString(CultureInfo.InvariantCulture), d.MaxApdu.ToString(CultureInfo.InvariantCulture), d.Segmentation.ToString());
            }

            AnsiConsole.Write(table);
        }

        return 0;
    }
}

internal sealed class BacnetObjectsCommand : AsyncCommand<BacnetObjectsCommand.Settings>
{
    public sealed class Settings : BacnetSettings
    {
        [CommandArgument(0, "<device>"), Description("Device instance.")]
        public uint Device { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner) = await BacnetUi.OpenAsync(s, writes: false, ct);
        await using (owner)
        {
            await BacnetUi.EnsureAsync(client, s, s.Device, ct);
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Object", "Name", "Present value", "Units / states", "Status flags");
            foreach (var obj in (await client.ReadObjectListAsync(s.Device, ct)).Where(o => o.Type != BacnetObjectType.Device))
            {
                var r = await client.ReadPropertiesAsync(s.Device, obj, [BacnetPropertyId.ObjectName, BacnetPropertyId.PresentValue, BacnetPropertyId.Units, BacnetPropertyId.StatusFlags], ct);
                var units = r[2].Ok ? BacnetText.Unit((BacnetUnits)(uint)r[2].Values![0].Value!) : "";
                table.AddRow(obj.ShortName, Markup.Escape(r[0].Ok ? r[0].Values![0].ToString() : ""), r[1].Ok ? BacnetUi.Format(r[1].Values!) : "", Markup.Escape(units), r[3].Ok ? r[3].Values![0].ToString() : "");
            }

            AnsiConsole.Write(table);
        }

        return 0;
    }
}

internal sealed class BacnetReadCommand : AsyncCommand<BacnetReadCommand.Settings>
{
    public sealed class Settings : BacnetSettings
    {
        [CommandArgument(0, "<device>"), Description("Device instance.")]
        public uint Device { get; init; }

        [CommandArgument(1, "<object>"), Description("Object, e.g. AI1, analog-value:2, device:2604.")]
        public string Object { get; init; } = "";

        [CommandArgument(2, "[property]"), Description("Property (default present-value), e.g. object-name, priority-array, all.")]
        public string? Property { get; init; }

        [CommandOption("-i|--index"), Description("Array index.")]
        public uint? Index { get; init; }
    }

    internal static BacnetPropertyId ParseProperty(string? text)
    {
        if (string.IsNullOrEmpty(text)) return BacnetPropertyId.PresentValue;
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return (BacnetPropertyId)n;
        var key = text.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return Enum.GetValues<BacnetPropertyId>().FirstOrDefault(p => p.ToString().Equals(key, StringComparison.OrdinalIgnoreCase), (BacnetPropertyId)uint.MaxValue) is var p && (uint)p != uint.MaxValue
            ? p : throw new FormatException($"Unknown property '{text}'.");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner) = await BacnetUi.OpenAsync(s, writes: false, ct);
        await using (owner)
        {
            await BacnetUi.EnsureAsync(client, s, s.Device, ct);
            var obj = BacnetObjectId.Parse(s.Object);
            var prop = ParseProperty(s.Property);
            if (prop is BacnetPropertyId.All or BacnetPropertyId.Required or BacnetPropertyId.Optional)
            {
                var table = new Table().Border(TableBorder.Rounded).AddColumns("Property", "Value");
                foreach (var r in await client.ReadPropertiesAsync(s.Device, obj, [prop], ct))
                    table.AddRow(r.Reference.Property.ToString(), Markup.Escape(r.Ok ? BacnetUi.Format(r.Values!) : $"{r.ErrorClass}/{r.ErrorCode}"));
                AnsiConsole.Write(table);
                return 0;
            }

            var values = await client.ReadPropertyAsync(s.Device, obj, prop, s.Index, ct);
            Ui.Success($"{obj.ShortName}.{prop}{(s.Index is { } i ? $"[{i}]" : "")} = {Markup.Escape(BacnetUi.Format(values))}");
        }

        return 0;
    }
}

internal sealed class BacnetWriteCommand : AsyncCommand<BacnetWriteCommand.Settings>
{
    public sealed class Settings : BacnetSettings
    {
        [CommandArgument(0, "<device>"), Description("Device instance.")]
        public uint Device { get; init; }

        [CommandArgument(1, "<object>"), Description("Object, e.g. BO1, AV1, MSV1.")]
        public string Object { get; init; } = "";

        [CommandArgument(2, "<value>"), Description("Value: a number, active/inactive, true/false, or null to relinquish.")]
        public string Value { get; init; } = "";

        [CommandOption("-p|--priority"), Description("Priority 1–16 (default 16; operators usually use 8).")]
        public byte? Priority { get; init; }

        [CommandOption("--property"), Description("Property (default present-value).")]
        public string? Property { get; init; }

        [CommandOption("--allow-write"), Description("Required: writes start fans, move valves and change set points.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the confirmation.")]
        public bool Yes { get; init; }
    }

    private static BacnetValue Parse(BacnetObjectId obj, string text)
    {
        var t = text.ToLowerInvariant();
        if (t == "null") return BacnetValue.Null;
        return obj.Type switch
        {
            BacnetObjectType.BinaryOutput or BacnetObjectType.BinaryValue or BacnetObjectType.BinaryInput =>
                BacnetValue.Enumerated(t is "1" or "active" or "on" or "true" or "start" ? 1u : t is "0" or "inactive" or "off" or "false" or "stop" ? 0u : throw new FormatException("Binary objects take active/inactive (1/0).")),
            BacnetObjectType.MultiStateOutput or BacnetObjectType.MultiStateValue or BacnetObjectType.MultiStateInput => BacnetValue.Unsigned(uint.Parse(text, CultureInfo.InvariantCulture)),
            _ => BacnetValue.Real(float.Parse(text, CultureInfo.InvariantCulture)),
        };
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Error("Writes start fans, move valves and change set points. Add --allow-write.");
            return 2;
        }

        var (client, owner) = await BacnetUi.OpenAsync(s, writes: true, ct);
        await using (owner)
        {
            await BacnetUi.EnsureAsync(client, s, s.Device, ct);
            var obj = BacnetObjectId.Parse(s.Object);
            var prop = BacnetReadCommand.ParseProperty(s.Property);
            var value = Parse(obj, s.Value);
            if (!s.Yes && !s.Sim && !AnsiConsole.Confirm($"Write {Markup.Escape(value.ToString())} to {obj.ShortName}.{prop} on device {s.Device}{(s.Priority is { } p ? $" at priority {p}" : "")}?", false)) return 1;
            await client.WritePropertyAsync(s.Device, obj, prop, value, s.Priority, ct);
            Ui.Success($"Written. {obj.ShortName}.{prop} now reads {Markup.Escape(BacnetUi.Format(await client.ReadPropertyAsync(s.Device, obj, prop, ct: ct)))}.");
        }

        return 0;
    }
}

internal sealed class BacnetSubscribeCommand : AsyncCommand<BacnetSubscribeCommand.Settings>
{
    public sealed class Settings : BacnetSettings
    {
        [CommandArgument(0, "<device>"), Description("Device instance.")]
        public uint Device { get; init; }

        [CommandArgument(1, "<objects>"), Description("Comma-separated objects, e.g. AI1,AI4,BI1.")]
        public string Objects { get; init; } = "";

        [CommandOption("--confirmed"), Description("Ask for confirmed notifications.")]
        public bool Confirmed { get; init; }

        [CommandOption("--seconds"), Description("Stop after this many seconds (default: until Ctrl+C).")]
        public int? Seconds { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var (client, owner) = await BacnetUi.OpenAsync(s, writes: false, ct);
        await using (owner)
        {
            await BacnetUi.EnsureAsync(client, s, s.Device, ct);
            var subs = new List<IAsyncDisposable>();
            foreach (var o in s.Objects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                subs.Add(await client.SubscribeCovAsync(s.Device, BacnetObjectId.Parse(o), n =>
                    AnsiConsole.MarkupLine($"[{Ui.Hex(Ui.Muted)}]{DateTime.Now:HH:mm:ss}[/] [{Ui.Hex(Ui.Amber)}]COV[/] {n.Object.ShortName} " + Markup.Escape(string.Join("  ", n.Values.Select(v => $"{v.Reference.Property}={BacnetUi.Format(v.Values)}")))),
                    s.Confirmed, lifetime: 600, ct: ct));
            Ui.Success($"Subscribed to {subs.Count} object(s). Ctrl+C to cancel the subscriptions.");
            try
            {
                await Task.Delay(s.Seconds is { } sec ? TimeSpan.FromSeconds(sec) : Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
            }

            foreach (var sub in subs) await sub.DisposeAsync();
        }

        return 0;
    }
}

internal sealed class BacnetServeCommand : AsyncCommand<BacnetServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--device"), Description("Device instance (default 2604).")]
        public uint Device { get; init; } = 2604;

        [CommandOption("-p|--port"), Description("UDP port (default 47808).")]
        public int Port { get; init; } = BacnetDeviceOptions.DefaultPort;

        [CommandOption("--broadcast"), Description("Broadcast address for I-Am (default 255.255.255.255).")]
        public string? Broadcast { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var ahu = BacnetAhuSimulator.Create(o =>
        {
            o.DeviceInstance = s.Device;
            o.UseDatagramTransport(() => new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, s.Port), reuseAddress: true) { EnableBroadcast = true });
            o.BroadcastAddress = new IPEndPoint(s.Broadcast is null ? IPAddress.Broadcast : IPAddress.Parse(s.Broadcast), s.Port);
        });
        ahu.Device.PropertyWritten += (o, p, pr) => Ui.Warn($"{o.Id.ShortName}.{p} written{(pr is { } x ? $" at priority {x}" : "")}: {Markup.Escape(o.FormatValue())}");
        await ahu.StartAsync(ct);
        Ui.Success($"BACnet/IP device {s.Device} \"AHU-1 Lantai 3\" on UDP {s.Port} with {ahu.Device.Objects.Count} objects. Point a BMS or YABE at it. Ctrl+C to stop.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                ahu.Step(5);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}

internal sealed class BacnetDemoCommand : AsyncCommand<BacnetDemoCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--allow-write"), Description("Also stop the fan at priority 8, change a set point and relinquish.")]
        public bool AllowWrite { get; init; }

        [CommandOption("--frames"), Description("Print every BACnet/IP datagram.")]
        public bool Frames { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        var settings = new BacnetSettings { Sim = true, Frames = s.Frames };
        var (client, owner) = await BacnetUi.OpenAsync(settings, s.AllowWrite, ct);
        await using (owner)
        {
            foreach (var d in await client.DiscoverAsync(TimeSpan.FromMilliseconds(400), ct: ct)) Ui.Success($"I-Am: {Markup.Escape(d.ToString())}");
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Object", "Name", "Present value");
            foreach (var obj in (await client.ReadObjectListAsync(2604, ct)).Where(o => o.Type != BacnetObjectType.Device))
            {
                var r = await client.ReadPropertiesAsync(2604, obj, [BacnetPropertyId.ObjectName, BacnetPropertyId.PresentValue], ct);
                table.AddRow(obj.ShortName, Markup.Escape(r[0].Values![0].ToString()), BacnetUi.Format(r[1].Values!));
            }

            AnsiConsole.Write(table);
            await using (await client.SubscribeCovAsync(2604, BacnetObjectId.Parse("AI1"), n => AnsiConsole.MarkupLine($"  [{Ui.Hex(Ui.Amber)}]COV[/] AI1 supply air = {BacnetUi.Format(n.Values[0].Values)} °C"), ct: ct))
            {
                try
                {
                    await client.WritePropertyAsync(2604, BacnetObjectId.Parse("AV1"), BacnetPropertyId.PresentValue, BacnetValue.Real(12f), 8, ct);
                    await client.WritePropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Enumerated(0), 8, ct);
                    var array = await client.ReadPropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PriorityArray, ct: ct);
                    Ui.Success($"BO1 priority array: {Markup.Escape(string.Join(" ", array.Select((v, i) => v.Tag == BacnetApplicationTag.Null ? "·" : $"{i + 1}:{v}")))}");
                    await Task.Delay(3000, ct);
                    await client.WritePropertyAsync(2604, BacnetObjectId.Parse("BO1"), BacnetPropertyId.PresentValue, BacnetValue.Null, 8, ct);
                    Ui.Success("BO1 relinquished at priority 8: the fan follows its relinquish default again.");
                }
                catch (ReadOnlyModeException ex)
                {
                    Ui.Warn(Markup.Escape(ex.Message) + " Add --allow-write to try it.");
                    await Task.Delay(3000, ct);
                }
            }
        }

        return 0;
    }
}

internal sealed class BacnetDecodeCommand : Command<BacnetDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("A BACnet/IP datagram (81 …) in hex.")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        AnsiConsole.Write(Ui.FrameLane(bytes, BacnetPacket.DescribeFields(bytes)));
        var p = BacnetPacket.Decode(bytes);
        Ui.Success(Markup.Escape(p.Describe()));
        try
        {
            switch (p.PduType, p.Service)
            {
                case (BacnetPduType.UnconfirmedRequest, (byte)BacnetUnconfirmedService.IAm):
                    var iam = BacnetServices.DecodeIAm(p.Data);
                    Ui.Success($"device {iam.Device}, max APDU {iam.MaxApdu}, segmentation {iam.Segmentation}, vendor {iam.VendorId}");
                    break;
                case (BacnetPduType.ConfirmedRequest, (byte)BacnetConfirmedService.ReadProperty):
                    var rp = BacnetServices.DecodeReadProperty(p.Data);
                    Ui.Success($"{rp.Object.ShortName}.{rp.Property.Property}");
                    break;
                case (BacnetPduType.ComplexAck, (byte)BacnetConfirmedService.ReadProperty):
                    var ack = BacnetServices.DecodeReadPropertyAck(p.Data);
                    Ui.Success($"{ack.Object.ShortName}.{ack.Property.Property} = {Markup.Escape(BacnetUi.Format(ack.Values))}");
                    break;
                case (BacnetPduType.ConfirmedRequest, (byte)BacnetConfirmedService.WriteProperty):
                    var wp = BacnetServices.DecodeWriteProperty(p.Data);
                    Ui.Success($"{wp.Object.ShortName}.{wp.Property.Property} ← {Markup.Escape(BacnetUi.Format(wp.Values))}{(wp.Priority is { } pr ? $" @ priority {pr}" : "")}");
                    break;
                case (BacnetPduType.UnconfirmedRequest, (byte)BacnetUnconfirmedService.UnconfirmedCovNotification) or (BacnetPduType.ConfirmedRequest, (byte)BacnetConfirmedService.ConfirmedCovNotification):
                    var cov = BacnetServices.DecodeCovNotification(p.Data);
                    Ui.Success($"COV {cov.Object.ShortName} from device {cov.Device.Instance}: " + Markup.Escape(string.Join("  ", cov.Values.Select(v => $"{v.Reference.Property}={BacnetUi.Format(v.Values)}"))));
                    break;
            }
        }
        catch (ProtocolException ex)
        {
            Ui.Error(Markup.Escape(ex.Message));
            return 1;
        }

        return 0;
    }
}
