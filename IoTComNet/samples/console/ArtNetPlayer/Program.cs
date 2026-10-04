// ArtNetPlayer — play a rainbow chase on DMX fixtures over Art-Net (or sACN).
//
//   dotnet run -- --simulate                  # sends to a local receiver and prints what arrives
//   dotnet run -- --universe 0                # broadcast to your Art-Net nodes
//   dotnet run -- --sacn --universe 1         # sACN / E1.31 multicast instead
//
// Each fixture is RGB on 3 consecutive channels starting at channel 1.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Dmx;

string? Get(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var simulate = args.Contains("--simulate");
var useSacn = args.Contains("--sacn");
var universe = int.Parse(Get("--universe") ?? (useSacn ? "1" : "0"));
var fixtures = int.Parse(Get("--fixtures") ?? "8");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Simulation: a receiving node on loopback that draws the levels it gets.
await using var receiver = simulate ? (DmxNodeBase)(useSacn ? SacnNode.Create(o => o.Bind(IPAddress.Loopback, 0)) : ArtNetNode.Create(o => o.Bind(IPAddress.Loopback, 0))) : null;
if (receiver is not null)
{
    await receiver.StartAsync();
    receiver.DmxReceived += frame => Console.Write($"\r{Render(frame.Data.Span, fixtures)}  seq {frame.Sequence,3}");
}

await using DmxNodeBase sender = useSacn
    ? SacnNode.Create(o => { o.Port = 0; o.Name = "IoTCom ArtNetPlayer"; if (receiver is not null) o.SendTo(receiver.LocalEndPoint!); })
    : ArtNetNode.Create(o => { o.Port = 0; o.RespondToPoll = false; if (receiver is not null) o.SendTo(receiver.LocalEndPoint!); });
await sender.StartAsync();

Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · {(useSacn ? "sACN" : "Art-Net")} universe {universe}, {fixtures} RGB fixtures at 40 fps. Ctrl+C to stop.");
var levels = new byte[fixtures * 3];
using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
var t = 0.0;
try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        t += 0.025;
        for (var f = 0; f < fixtures; f++)
        {
            var hue = (t * 60 + f * 360.0 / fixtures) % 360;
            var (r, g, b) = HsvToRgb(hue, 1, 0.5 + 0.5 * Math.Sin(t * 3 + f));
            (levels[f * 3], levels[f * 3 + 1], levels[f * 3 + 2]) = (r, g, b);
        }
        await sender.SendDmxAsync(universe, levels, ct: cts.Token);
    }
}
catch (OperationCanceledException) { }
Console.WriteLine("\nBlackout.");
await sender.SendDmxAsync(universe, new byte[levels.Length]);

static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
{
    var c = v * s;
    var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
    var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
    var m = v - c;
    return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
}

static string Render(ReadOnlySpan<byte> data, int fixtures)
{
    var sb = new System.Text.StringBuilder();
    for (var f = 0; f < fixtures && f * 3 + 2 < data.Length; f++)
        sb.Append($"\e[48;2;{data[f * 3]};{data[f * 3 + 1]};{data[f * 3 + 2]}m    \e[0m ");
    return sb.ToString();
}
