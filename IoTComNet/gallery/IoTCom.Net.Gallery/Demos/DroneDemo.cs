using System.Collections.ObjectModel;
using System.Net;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Drone telemetry over MAVLink: a simulated quadcopter and the Gallery as ground station on an in-memory UDP link.
/// Attitude drives an artificial horizon, position draws a track, and arm / takeoff / RTL / land go out as COMMAND_LONG
/// with acknowledgement — only after "Allow commands" is ticked.
/// </summary>
public sealed partial class DroneDemo : GalleryDemo
{
    public override string Id => "mavlink-drone";
    public override Text Title => new("Drone telemetry over MAVLink", "Telemetri drone lewat MAVLink");
    public override Text Summary => new(
        "A ground station talks MAVLink 2 to a simulated quadcopter: artificial horizon, flight track, battery and status texts, and acknowledged commands to arm, take off, return and land.",
        "Ground station berbicara MAVLink 2 dengan quadcopter simulasi: artificial horizon, jejak terbang, baterai dan pesan status, serta perintah ber-ACK untuk arm, lepas landas, pulang, dan mendarat.");
    public override Text Docs => new(
        "MAVLink is the telemetry and command protocol of PX4, ArduPilot and most drones, rovers and boats. Every frame carries the sender's system and component id, a sequence number (gaps reveal lost packets) and a message id; the payload layout and a CRC seed (CRC_EXTRA) come from the dialect XML.\n\nIoTCom.Net compiles the official common dialect — 235 messages — with a Roslyn source generator, so ATTITUDE, GLOBAL_POSITION_INT or COMMAND_LONG are plain C# classes; add your own XML to generate your own messages.\n\nThe vehicle streams HEARTBEAT (1 Hz), ATTITUDE (10 Hz), GLOBAL_POSITION_INT (5 Hz), VFR_HUD, SYS_STATUS and BATTERY_STATUS. Commands go out as COMMAND_LONG and are retried until a COMMAND_ACK arrives; the vehicle refuses to arm on a low battery (watch the PreArm status text) and to disarm in flight.",
        "MAVLink adalah protokol telemetri dan perintah PX4, ArduPilot, dan sebagian besar drone, rover, serta kapal. Setiap frame membawa system dan component id pengirim, nomor urut (celahnya menunjukkan paket hilang), dan message id; tata letak payload dan seed CRC (CRC_EXTRA) berasal dari XML dialek.\n\nIoTCom.Net mengompilasi dialek common resmi — 235 pesan — dengan source generator Roslyn, sehingga ATTITUDE, GLOBAL_POSITION_INT, atau COMMAND_LONG menjadi kelas C# biasa; tambahkan XML Anda sendiri untuk menghasilkan pesan Anda sendiri.\n\nKendaraan mengirim HEARTBEAT (1 Hz), ATTITUDE (10 Hz), GLOBAL_POSITION_INT (5 Hz), VFR_HUD, SYS_STATUS, dan BATTERY_STATUS. Perintah dikirim sebagai COMMAND_LONG dan diulang sampai COMMAND_ACK tiba; kendaraan menolak arm saat baterai lemah (lihat pesan status PreArm) dan menolak disarm saat terbang.");
    public override string Category => "Navigation";
    public override IReadOnlyList<string> Protocols => ["MAVLink 2", "Source generator", "UDP"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/mavlink.md";

    private static readonly IPEndPoint GcsAddress = new(IPAddress.Loopback, 14550);
    private static readonly IPEndPoint VehicleAddress = new(IPAddress.Parse("192.168.4.1"), 14555);
    private MavlinkConnection? _gcs, _vehicle;
    private MavlinkVehicleSimulator? _sim;
    private MavlinkGroundStation? _station;
    private CancellationTokenSource? _cts;

    /// <summary>Track in metres north/east of home.</summary>
    public ObservableCollection<Point> Track { get; } = [];

    /// <summary>Recent status texts.</summary>
    public ObservableCollection<string> Texts { get; } = [];

    [ObservableProperty] private double _roll;
    [ObservableProperty] private double _pitch;
    [ObservableProperty] private double _heading;
    [ObservableProperty] private double _altitude;
    [ObservableProperty] private double _groundSpeed;
    [ObservableProperty] private double _climb;
    [ObservableProperty] private double _voltage;
    [ObservableProperty] private int _batteryPercent;
    [ObservableProperty] private bool _armed;
    [ObservableProperty] private string _mode = "—";
    [ObservableProperty] private int _satellites;
    [ObservableProperty] private long _packets;
    [ObservableProperty] private long _lost;
    [ObservableProperty] private bool _allowCommands;
    [ObservableProperty] private bool _lowBattery;
    [ObservableProperty] private Point _position;

    partial void OnAllowCommandsChanged(bool value)
    {
        if (_station is not null) _station.ReadOnly = !value;
    }

    partial void OnLowBatteryChanged(bool value) => _sim?.SetBattery(value ? 14.3 : 16.4);

    protected override async Task OnStartAsync()
    {
        var net = new InMemoryDatagramNetwork(seed: 9);
        // Vehicle: system 1, component 1, sending to the ground station's port 14550 (as a telemetry radio would).
        _vehicle = MavlinkConnection.Create(o =>
        {
            o.UseInMemory(net, VehicleAddress).SendTo(GcsAddress);
            (o.SystemId, o.ComponentId) = (1, 1);
        });
        _sim = new MavlinkVehicleSimulator(_vehicle);
        if (LowBattery) _sim.SetBattery(14.3);
        // Ground station: listens on 14550 and answers whoever spoke last.
        _gcs = MavlinkConnection.Create(o => o.UseInMemory(net, GcsAddress));
        _gcs.AddTap(Tap);
        _station = new MavlinkGroundStation(_gcs) { ReadOnly = !AllowCommands, Timeout = TimeSpan.FromMilliseconds(600) };
        _station.StatusText += (_, text) => Ui(() =>
        {
            Texts.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
            while (Texts.Count > 8) Texts.RemoveAt(Texts.Count - 1);
        });
        await _vehicle.ConnectAsync();
        await _gcs.ConnectAsync();
        _sim.Start();
        Ui(() => { Track.Clear(); Texts.Clear(); });

        _cts = new CancellationTokenSource();
        _ = RefreshAsync(_cts.Token);
        SetStatus(new Text("Linked to system 1 over MAVLink 2 (UDP 14550). Tick \"Allow commands\" to fly.", "Terhubung ke system 1 lewat MAVLink 2 (UDP 14550). Centang \"Izinkan perintah\" untuk terbang."));
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var n = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var s = _station!.State;
                var home = (_sim!.HomeLatitude, _sim.HomeLongitude);
                var north = (s.Latitude - home.HomeLatitude) * Math.PI / 180 * 6_371_000;
                var east = (s.Longitude - home.HomeLongitude) * Math.PI / 180 * 6_371_000 * Math.Cos(home.HomeLatitude * Math.PI / 180);
                var addPoint = s.Latitude != 0 && n++ % 3 == 0 && s.RelativeAltitude > 0.2;
                Ui(() =>
                {
                    (Roll, Pitch, Heading) = (s.Roll, s.Pitch, s.Yaw);
                    (Altitude, GroundSpeed, Climb) = (s.RelativeAltitude, s.GroundSpeed, s.Climb);
                    (Voltage, BatteryPercent, Armed, Satellites) = (s.BatteryVoltage, Math.Max(0, s.BatteryRemaining), s.Armed, s.Satellites);
                    Mode = s.Heartbeat is null ? "—" : MavlinkVehicleSimulator.ModeName(s.CustomMode);
                    (Packets, Lost) = (_gcs!.Statistics.PacketsReceived, _gcs.Statistics.PacketsLost);
                    if (s.Latitude != 0) Position = new Point(east, north);
                    if (addPoint)
                    {
                        Track.Add(new Point(east, north));
                        if (Track.Count > 600) Track.RemoveAt(0);
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Arm (or disarm).</summary>
    public Task ArmAsync(bool arm) => Command(st => st.ArmAsync(arm), arm ? "ARM" : "DISARM");

    /// <summary>Take off to 20 m; the simulator then flies its survey circuit.</summary>
    public Task TakeoffAsync() => Command(st => st.TakeoffAsync(20), "TAKEOFF 20 m");

    /// <summary>Return to launch.</summary>
    public Task ReturnAsync() => Command(st => st.ReturnToLaunchAsync(), "RTL");

    /// <summary>Land.</summary>
    public Task LandAsync() => Command(st => st.LandAsync(), "LAND");

    private async Task Command(Func<MavlinkGroundStation, Task<MavResult>> send, string label)
    {
        if (_station is null) return;
        try
        {
            var result = await send(_station);
            SetStatus(new Text($"{label} → COMMAND_ACK {result}", $"{label} → COMMAND_ACK {result}"));
        }
        catch (ReadOnlyModeException)
        {
            SetStatus(new Text("Blocked: the ground station is read-only. Tick \"Allow commands\".", "Diblokir: ground station read-only. Centang \"Izinkan perintah\"."));
        }
        catch (IoTComException ex)
        {
            Status = ex.Message;
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _station?.Dispose();
        if (_sim is not null) await _sim.DisposeAsync();
        if (_gcs is not null) await _gcs.DisposeAsync();
        if (_vehicle is not null) await _vehicle.DisposeAsync();
        _cts?.Dispose();
        (_station, _sim, _gcs, _vehicle, _cts) = (null, null, null, null, null);
        Status = "";
    }
}
