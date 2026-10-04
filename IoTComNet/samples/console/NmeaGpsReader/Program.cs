// NmeaGpsReader — read a GPS/GNSS receiver and print position, speed and satellites.
//
//   dotnet run -- --simulate              # built-in simulated receiver driving around Bandung
//   dotnet run -- --serial COM4 --baud 9600
//   dotnet run -- --host 192.168.1.50 --port 10110   # NMEA-over-TCP (chart plotters, AIS gateways)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net;
using IoTCom.Net.Protocols.Nmea;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;

string? Get(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var simulate = args.Contains("--simulate") || args.Length == 0;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Simulation: an NmeaServer fed by NmeaSimulator, reachable in-process.
var listener = new InMemoryTransportListener("gps");
await using var gpsServer = simulate ? NmeaServer.Create(o => o.ListenInMemory(listener)) : null;

await using var gps = NmeaReader.Create(o =>
{
    if (simulate) o.UseInMemory(listener);
    else if (Get("--serial") is { } port) o.UseSerial(port, int.Parse(Get("--baud") ?? "9600"));
    else o.UseTcp(Get("--host") ?? "127.0.0.1", int.Parse(Get("--port") ?? "10110"));
});

if (gpsServer is not null)
{
    await gpsServer.StartAsync();
    _ = new NmeaSimulator().RunAsync(gpsServer, TimeSpan.FromSeconds(1), cts.Token);
}
await gps.ConnectAsync(cts.Token);
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · NMEA 0183 reader ({(simulate ? "simulated receiver" : "live")}). Ctrl+C to stop.\n");

// Typed stream: only RMC (recommended minimum) messages; GnssState merges every sentence type.
try
{
    await foreach (var rmc in gps.ReadAsync<RmcMessage>(cts.Token))
    {
        var fix = gps.Gnss.Current;
        if (!fix.HasFix)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  searching… {fix.SatellitesInView.Count} satellites in view");
            continue;
        }
        var strongest = fix.SatellitesInView.OrderByDescending(s => s.Snr ?? 0).Take(3).Select(s => $"PRN{s.Prn}:{s.Snr}dB");
        Console.WriteLine(
            $"{rmc.Timestamp:HH:mm:ss}Z  {fix.Latitude,10:0.000000}, {fix.Longitude,11:0.000000}  " +
            $"{fix.SpeedKmh,5:0.0} km/h  {fix.CourseDegrees,3:0}°  alt {fix.AltitudeMeters:0} m  " +
            $"sats {fix.SatellitesUsed}/{fix.SatellitesInView.Count} HDOP {fix.Hdop:0.0}  [{string.Join(' ', strongest)}]");
    }
}
catch (OperationCanceledException) { }

Console.WriteLine($"Dropped sentences (bad checksum): {gps.DroppedSentences}");
