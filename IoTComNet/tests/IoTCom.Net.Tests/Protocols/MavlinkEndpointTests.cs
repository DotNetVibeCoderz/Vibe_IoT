using System.Net;
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public sealed class MavlinkEndpointTests
{
    private static readonly IPEndPoint Gcs = new(IPAddress.Loopback, 14550);
    private static readonly IPEndPoint Vehicle = new(IPAddress.Parse("10.0.0.1"), 14555);

    private static async Task<(MavlinkConnection Gcs, MavlinkConnection Vehicle, MavlinkVehicleSimulator Sim, MavlinkGroundStation Station, InMemoryDatagramNetwork Net)> RigAsync()
    {
        var net = new InMemoryDatagramNetwork(seed: 3);
        var gcs = MavlinkConnection.Create(o => o.UseInMemory(net, Gcs));                 // listens; replies to whoever spoke last
        var vehicle = MavlinkConnection.Create(o =>
        {
            o.UseInMemory(net, Vehicle).SendTo(Gcs);
            (o.SystemId, o.ComponentId) = (1, 1);
        });
        var sim = new MavlinkVehicleSimulator(vehicle);   // attach before connecting: the first heartbeat is already the vehicle's
        await gcs.ConnectAsync();
        await vehicle.ConnectAsync();
        sim.Start();
        var station = new MavlinkGroundStation(gcs) { Timeout = TimeSpan.FromMilliseconds(400) };
        Assert.True(await station.WaitForHeartbeatAsync(TimeSpan.FromSeconds(5)));
        return (gcs, vehicle, sim, station, net);
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout, string what)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Ground_station_flies_the_simulated_quadcopter()
    {
        var (gcs, vehicle, sim, station, _) = await RigAsync();
        await using (gcs)
        await using (vehicle)
        await using (sim)
        using (station)
        {
            var texts = new List<string>();
            station.StatusText += (_, t) => { lock (texts) texts.Add(t); };
            await Until(() => station.State.Satellites > 0 && station.State.Latitude != 0, TimeSpan.FromSeconds(3), "telemetry");
            Assert.Equal(-6.9147, station.State.Latitude, 3);
            Assert.Equal(MavType.Quadrotor, station.State.Heartbeat!.Type);

            // Pre-arm check: battery too low.
            sim.SetBattery(14.2);
            Assert.Equal(MavResult.Denied, await station.ArmAsync());
            await Until(() => { lock (texts) return texts.Any(t => t.StartsWith("PreArm", StringComparison.Ordinal)); }, TimeSpan.FromSeconds(2), "PreArm text");
            sim.SetBattery(16.6);

            Assert.Equal(MavResult.TemporarilyRejected, await station.TakeoffAsync(8));
            Assert.Equal(MavResult.Accepted, await station.ArmAsync());
            await Until(() => station.State.Armed, TimeSpan.FromSeconds(3), "armed heartbeat");
            Assert.Equal(MavResult.Accepted, await station.TakeoffAsync(6));
            await Until(() => station.State.RelativeAltitude > 5, TimeSpan.FromSeconds(10), "climb");
            Assert.Equal(MavResult.Denied, await station.ArmAsync(false)); // flying: disarm refused

            Assert.Equal(400f, await station.SetParameterAsync("LAND_SPEED", 400));
            Assert.Equal(MavResult.Accepted, await station.LandAsync());
            await Until(() => !station.State.Armed && sim.Phase == SimulatedFlightPhase.Disarmed, TimeSpan.FromSeconds(10), "landing");
            Assert.True(gcs.Statistics.PacketsReceived > 100);
            // Regression: the simulator sends from several tasks; sequence numbers must still leave in order,
            // so a lossless link reports zero loss.
            Assert.Equal(0, gcs.Statistics.PacketsLost);
        }
    }

    [Fact]
    public async Task Parameters_download_completely_over_a_lossy_link()
    {
        var (gcs, vehicle, sim, station, net) = await RigAsync();
        await using (gcs)
        await using (vehicle)
        await using (sim)
        using (station)
        {
            net.LossRate = 0.3;
            var p = await station.ReadParametersAsync();
            Assert.Equal(12, p.Count);
            Assert.Equal(600f, p["WPNAV_SPEED"]);
            Assert.Equal(1500f, p["RTL_ALT"]);
            Assert.True(gcs.Statistics.PacketsLost > 0, "sequence gaps should reveal the loss");
        }
    }

    [Fact]
    public async Task Read_only_station_never_sends_commands()
    {
        var (gcs, vehicle, sim, station, _) = await RigAsync();
        await using (gcs)
        await using (vehicle)
        await using (sim)
        using (station)
        {
            station.ReadOnly = true;
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => station.ArmAsync());
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => station.SetParameterAsync("RTL_ALT", 3000));
            Assert.Equal(SimulatedFlightPhase.Disarmed, sim.Phase);
        }
    }

    [Fact]
    public async Task Works_over_real_udp_and_streams()
    {
        await using var gcs = MavlinkConnection.Create(o => o.UseUdp(0, IPAddress.Loopback));
        await gcs.ConnectAsync();
        var port = ((IPEndPoint)gcs.LocalEndPoint!).Port;
        await using var vehicle = MavlinkConnection.Create(o =>
        {
            o.UseUdp(0, IPAddress.Loopback).SendTo(new IPEndPoint(IPAddress.Loopback, port));
            (o.SystemId, o.ComponentId, o.HeartbeatType) = (7, 1, MavType.FixedWing);
        });
        await vehicle.ConnectAsync();
        var hb = await gcs.WaitForAsync<Heartbeat>((_, p) => p.SystemId == 7, TimeSpan.FromSeconds(3));
        Assert.Equal(MavType.FixedWing, hb!.Value.Message.Type);
        // The GCS answers the last peer: the vehicle sees our heartbeat (system 255) too.
        Assert.NotNull(await vehicle.WaitForAsync<Heartbeat>((_, p) => p.SystemId == 255, TimeSpan.FromSeconds(3)));

        var (a, b) = InMemoryTransport.CreatePair();
        await using var left = MavlinkConnection.Create(o => { o.UseInMemory(a); o.Version = MavlinkVersion.V1; });
        await using var right = MavlinkConnection.Create(o => { o.UseInMemory(b); o.SystemId = 1; });
        await left.ConnectAsync();
        await right.ConnectAsync();
        var p = await right.WaitForAsync<Heartbeat>((_, pk) => pk.SystemId == 255, TimeSpan.FromSeconds(3));
        Assert.Equal(MavlinkVersion.V1, p!.Value.Packet.Version);
    }
}
