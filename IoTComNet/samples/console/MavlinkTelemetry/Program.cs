// MavlinkTelemetry — a minimal read-only ground station: listens for MAVLink on UDP 14550 and prints telemetry.
//
//   dotnet run                       # also starts a simulated quadcopter that takes off and flies a circuit
//   dotnet run -- --no-sim           # listen only (PX4/ArduPilot SITL, a telemetry radio bridge, MAVProxy --out)
//
// Read-only: this sample never sends commands to a real vehicle. Only the built-in simulator is flown.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using System.Net;
using IoTCom.Net;
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;
using IoTCom.Net.Transports;

var simulate = !args.Contains("--no-sim");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var gcs = MavlinkConnection.Create(o => o.UseUdp(14550));
using var station = new MavlinkGroundStation(gcs) { ReadOnly = !simulate };
station.StatusText += (severity, text) => Console.WriteLine($"  [{severity}] {text}");
await gcs.ConnectAsync(cts.Token);
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · MAVLink ground station on udp://0.0.0.0:14550 ({CommonDialect.Instance.Messages.Count} messages known)");

// Optional simulated vehicle on another port, sending to us.
await using var vehicleLink = simulate ? MavlinkConnection.Create(o =>
{
    o.UseUdp(14555).SendTo(new IPEndPoint(IPAddress.Loopback, 14550));
    (o.SystemId, o.ComponentId) = (1, 1);
}) : null;
await using var sim = vehicleLink is null ? null : new MavlinkVehicleSimulator(vehicleLink);
if (vehicleLink is not null)
{
    await vehicleLink.ConnectAsync(cts.Token);
    sim!.Start();
}

if (!await station.WaitForHeartbeatAsync(TimeSpan.FromSeconds(10), cts.Token))
{
    Console.WriteLine("No vehicle heartbeat on UDP 14550.");
    return;
}
var hb = station.State.Heartbeat!;
Console.WriteLine($"Vehicle: system {station.TargetSystem}, {hb.Type}, autopilot {hb.Autopilot}\n");

if (sim is not null)
{
    // Simulator only: arm and take off so there is something to watch.
    Console.WriteLine($"ARM      → {await station.ArmAsync()}");
    Console.WriteLine($"TAKEOFF  → {await station.TakeoffAsync(15)}\n");
}

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        var s = station.State;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {(s.Armed ? "ARMED" : "disarmed"),-8} {MavlinkVehicleSimulator.ModeName(s.CustomMode),-9} " +
            $"alt {s.RelativeAltitude,5:0.0} m  gs {s.GroundSpeed,4:0.0} m/s  roll {s.Roll * 57.3,5:0.0}°  " +
            $"pos {s.Latitude:0.000000},{s.Longitude:0.000000}  bat {s.BatteryVoltage:0.00} V  lost {gcs.Statistics.PacketsLost}");
    }
}
catch (OperationCanceledException) { }
