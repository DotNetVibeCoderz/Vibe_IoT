using IoTCom.Net.Protocols.Mavlink.Common;

namespace IoTCom.Net.Protocols.Mavlink;

/// <summary>Flight phases of <see cref="MavlinkVehicleSimulator"/>.</summary>
public enum SimulatedFlightPhase
{
    /// <summary>On the ground, disarmed.</summary>
    Disarmed,
    /// <summary>On the ground, motors armed.</summary>
    Armed,
    /// <summary>Climbing to the takeoff altitude.</summary>
    TakingOff,
    /// <summary>Flying the survey circuit.</summary>
    Mission,
    /// <summary>Returning home.</summary>
    Returning,
    /// <summary>Descending to land.</summary>
    Landing,
}

/// <summary>
/// A simulated quadcopter (ArduCopter-like) that speaks MAVLink: heartbeat, attitude, position, GPS, VFR HUD, system and
/// battery status at realistic rates; COMMAND_LONG arm/disarm, takeoff, land and RTL with COMMAND_ACK and pre-arm checks;
/// the parameter protocol with a dozen parameters; status texts for events. Home defaults to Bandung, Indonesia.
/// </summary>
public sealed class MavlinkVehicleSimulator : IAsyncDisposable
{
    private const double EarthRadius = 6_371_000;
    private readonly MavlinkConnection _link;
    private readonly Lock _gate = new();
    private readonly Random _random;
    private readonly List<(string Name, float Value, MavParamType Type)> _params =
    [
        ("WPNAV_SPEED", 600, MavParamType.Real32), ("RTL_ALT", 1500, MavParamType.Real32), ("LAND_SPEED", 50, MavParamType.Real32),
        ("PILOT_SPEED_UP", 250, MavParamType.Real32), ("ANGLE_MAX", 3000, MavParamType.Real32), ("BATT_CAPACITY", 5200, MavParamType.Real32),
        ("BATT_LOW_VOLT", 14.4f, MavParamType.Real32), ("BATT_ARM_VOLT", 15.0f, MavParamType.Real32), ("FENCE_ENABLE", 0, MavParamType.Real32),
        ("FENCE_RADIUS", 300, MavParamType.Real32), ("SURVEY_RADIUS", 60, MavParamType.Real32), ("SYSID_THISMAV", 1, MavParamType.Real32),
    ];
    private readonly DateTime _boot = DateTime.UtcNow;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private double _north, _east, _alt, _vNorth, _vEast, _vUp, _heading, _orbitAngle, _takeoffAlt = 20, _battery = 16.6, _consumedMah;
    private float _roll, _pitch;

    /// <summary>
    /// Creates a simulator on <paramref name="link"/> (system id 1, component id 1). Create it before connecting the link so the very
    /// first heartbeat already describes the vehicle.
    /// </summary>
    public MavlinkVehicleSimulator(MavlinkConnection link, double homeLatitude = -6.9147, double homeLongitude = 107.6098, double homeAltitude = 768, int seed = 5)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        (HomeLatitude, HomeLongitude, HomeAltitude) = (homeLatitude, homeLongitude, homeAltitude);
        _random = new Random(seed);
        _link.PacketReceived += OnPacket;
        _link.HeartbeatOverride = BuildHeartbeat;
        _link.Options.HeartbeatType = MavType.Quadrotor;
    }

    /// <summary>Home latitude.</summary>
    public double HomeLatitude { get; }

    /// <summary>Home longitude.</summary>
    public double HomeLongitude { get; }

    /// <summary>Home altitude above MSL, m.</summary>
    public double HomeAltitude { get; }

    /// <summary>Current flight phase.</summary>
    public SimulatedFlightPhase Phase { get; private set; } = SimulatedFlightPhase.Disarmed;

    /// <summary>Battery voltage, V (4S pack).</summary>
    public double BatteryVoltage => _battery;

    /// <summary>Sets the battery voltage (e.g. to demonstrate the pre-arm check).</summary>
    public void SetBattery(double volts)
    {
        lock (_gate) _battery = volts;
    }

    /// <summary>Starts the 20 Hz physics and telemetry loop.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    private Heartbeat BuildHeartbeat()
    {
        var armed = Phase != SimulatedFlightPhase.Disarmed;
        // ArduCopter custom modes: 0 STABILIZE, 3 AUTO, 4 GUIDED, 6 RTL, 9 LAND.
        uint mode = Phase switch
        {
            SimulatedFlightPhase.TakingOff => 4,
            SimulatedFlightPhase.Mission => 3,
            SimulatedFlightPhase.Returning => 6,
            SimulatedFlightPhase.Landing => 9,
            _ => 0,
        };
        return new Heartbeat
        {
            Type = MavType.Quadrotor,
            Autopilot = MavAutopilot.Ardupilotmega,
            BaseMode = MavModeFlag.CustomModeEnabled | MavModeFlag.StabilizeEnabled | (armed ? MavModeFlag.SafetyArmed : 0),
            CustomMode = mode,
            SystemStatus = armed ? MavState.Active : MavState.Standby,
        };
    }

    /// <summary>ArduCopter flight-mode name for a custom_mode number.</summary>
    public static string ModeName(uint customMode) => customMode switch
    {
        0 => "STABILIZE", 2 => "ALT_HOLD", 3 => "AUTO", 4 => "GUIDED", 5 => "LOITER", 6 => "RTL", 9 => "LAND", 16 => "POSHOLD",
        _ => $"MODE {customMode}",
    };

    private float Param(string name) => _params.First(p => p.Name == name).Value;

    private void OnPacket(MavlinkPacket p)
    {
        switch (p.Message)
        {
            case CommandLong c when c.TargetSystem is 0 || c.TargetSystem == _link.Options.SystemId:
                _ = HandleCommandAsync(c);
                break;
            case ParamRequestList l when l.TargetSystem == _link.Options.SystemId:
                _ = SendAllParamsAsync();
                break;
            case ParamRequestRead r when r.TargetSystem == _link.Options.SystemId:
                var index = r.ParamIndex >= 0 ? r.ParamIndex : _params.FindIndex(x => x.Name == r.ParamId);
                if (index >= 0 && index < _params.Count) _ = SendParamAsync(index);
                break;
            case ParamSet s when s.TargetSystem == _link.Options.SystemId:
                var i = _params.FindIndex(x => x.Name == s.ParamId);
                if (i < 0) break; // unknown parameters are ignored (no response)
                lock (_gate) _params[i] = (_params[i].Name, s.ParamValue, _params[i].Type);
                _ = SendParamAsync(i);
                _ = TextAsync(MavSeverity.Info, $"{s.ParamId} set to {s.ParamValue:0.##}");
                break;
        }
    }

    private async Task SendAllParamsAsync()
    {
        for (var i = 0; i < _params.Count; i++) await SendParamAsync(i).ConfigureAwait(false);
    }

    private Task SendParamAsync(int index)
    {
        var (name, value, type) = _params[index];
        return _link.SendAsync(new ParamValue { ParamId = name, Value = value, ParamType = type, ParamCount = (ushort)_params.Count, ParamIndex = (ushort)index });
    }

    private Task TextAsync(MavSeverity severity, string text) => _link.SendAsync(new Statustext { Severity = severity, Text = text });

    private async Task HandleCommandAsync(CommandLong c)
    {
        var result = MavResult.Accepted;
        string? text = null;
        lock (_gate)
        {
            switch (c.Command)
            {
                case MavCmd.ComponentArmDisarm when c.Param1 >= 0.5f:
                    if (Phase != SimulatedFlightPhase.Disarmed) break;
                    if (_battery < Param("BATT_ARM_VOLT"))
                    {
                        result = MavResult.Denied;
                        text = $"PreArm: Battery {_battery:0.0}V below {Param("BATT_ARM_VOLT"):0.0}V";
                    }
                    else
                    {
                        Phase = SimulatedFlightPhase.Armed;
                        text = "Arming motors";
                    }
                    break;
                case MavCmd.ComponentArmDisarm:
                    if (Phase is SimulatedFlightPhase.Disarmed or SimulatedFlightPhase.Armed || Math.Abs(c.Param2 - 21196) < 0.5f)
                    {
                        Phase = SimulatedFlightPhase.Disarmed;
                        _alt = Math.Min(_alt, 0);
                        text = "Disarming motors";
                    }
                    else
                    {
                        result = MavResult.Denied;
                        text = "Disarm denied: flying (land first)";
                    }
                    break;
                case MavCmd.NavTakeoff:
                    if (Phase != SimulatedFlightPhase.Armed)
                    {
                        result = MavResult.TemporarilyRejected;
                        text = "Takeoff rejected: arm first";
                    }
                    else
                    {
                        _takeoffAlt = c.Param7 > 1 ? Math.Min(c.Param7, 120) : 20;
                        Phase = SimulatedFlightPhase.TakingOff;
                        text = $"Takeoff to {_takeoffAlt:0} m";
                    }
                    break;
                case MavCmd.NavLand:
                    if (Phase is SimulatedFlightPhase.TakingOff or SimulatedFlightPhase.Mission or SimulatedFlightPhase.Returning)
                    {
                        Phase = SimulatedFlightPhase.Landing;
                        text = "Landing";
                    }
                    else result = MavResult.TemporarilyRejected;
                    break;
                case MavCmd.NavReturnToLaunch:
                    if (Phase is SimulatedFlightPhase.TakingOff or SimulatedFlightPhase.Mission or SimulatedFlightPhase.Landing)
                    {
                        Phase = SimulatedFlightPhase.Returning;
                        text = "Returning to launch";
                    }
                    else result = MavResult.TemporarilyRejected;
                    break;
                default:
                    result = MavResult.Unsupported;
                    break;
            }
        }
        await _link.SendAsync(new CommandAck { Command = c.Command, Result = result, TargetSystem = _link.Options.SystemId == 1 ? (byte)255 : (byte)0 }).ConfigureAwait(false);
        if (text is not null) await TextAsync(result == MavResult.Accepted ? MavSeverity.Info : MavSeverity.Warning, text).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var tick = 0;
        try
        {
            await TextAsync(MavSeverity.Info, "IoTCom SITL quadcopter ready").ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                string? landed;
                lock (_gate) landed = Step(0.05);
                tick++;
                if (landed is not null) await TextAsync(MavSeverity.Info, landed).ConfigureAwait(false);
                if (tick % 2 == 0) await _link.SendAsync(AttitudeMessage(), ct).ConfigureAwait(false);       // 10 Hz
                if (tick % 4 == 0) await _link.SendAsync(PositionMessage(), ct).ConfigureAwait(false);       // 5 Hz
                if (tick % 5 == 0) await _link.SendAsync(HudMessage(), ct).ConfigureAwait(false);            // 4 Hz
                if (tick % 10 == 0) await _link.SendAsync(GpsMessage(), ct).ConfigureAwait(false);           // 2 Hz
                if (tick % 20 == 0)
                {
                    await _link.SendAsync(SystemStatus(), ct).ConfigureAwait(false);                         // 1 Hz
                    await _link.SendAsync(BatteryMessage(), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Physics step; returns a status text when the vehicle touched down.</summary>
    private string? Step(double dt)
    {
        var speed = Param("WPNAV_SPEED") / 100.0;
        double targetN = _north, targetE = _east, targetUp = 0;
        switch (Phase)
        {
            case SimulatedFlightPhase.TakingOff:
                targetUp = Math.Min(Param("PILOT_SPEED_UP") / 100.0, (_takeoffAlt - _alt) * 0.8);
                if (_alt >= _takeoffAlt - 0.3) Phase = SimulatedFlightPhase.Mission;
                break;
            case SimulatedFlightPhase.Mission:
                // A survey circuit around home.
                var radius = Math.Max(10, Param("SURVEY_RADIUS"));
                _orbitAngle += speed / radius * dt;
                (targetN, targetE) = (radius * Math.Cos(_orbitAngle), radius * Math.Sin(_orbitAngle));
                targetUp = (_takeoffAlt - _alt) * 0.5;
                break;
            case SimulatedFlightPhase.Returning:
                var rtlAlt = Param("RTL_ALT") / 100.0;
                targetUp = (Math.Max(rtlAlt, _alt > rtlAlt ? _alt : rtlAlt) - _alt) * 0.5;
                (targetN, targetE) = (0, 0);
                if (Math.Sqrt(_north * _north + _east * _east) < 1.5) Phase = SimulatedFlightPhase.Landing;
                break;
            case SimulatedFlightPhase.Landing:
                targetUp = -Math.Max(0.3, Param("LAND_SPEED") / 100.0 * (_alt > 10 ? 3 : 1));
                break;
        }

        // Horizontal: steer towards the target at most at WPNAV_SPEED.
        var dn = targetN - _north;
        var de = targetE - _east;
        var dist = Math.Sqrt(dn * dn + de * de);
        var desired = Phase is SimulatedFlightPhase.Mission or SimulatedFlightPhase.Returning ? Math.Min(speed, dist * 0.6) : 0;
        var wantN = dist > 0.01 ? dn / dist * desired : 0;
        var wantE = dist > 0.01 ? de / dist * desired : 0;
        var accN = Math.Clamp((wantN - _vNorth) * 1.5, -4, 4);
        var accE = Math.Clamp((wantE - _vEast) * 1.5, -4, 4);
        _vNorth += accN * dt;
        _vEast += accE * dt;
        _vUp += (targetUp - _vUp) * Math.Min(1, 3 * dt);
        if (Phase is SimulatedFlightPhase.Disarmed or SimulatedFlightPhase.Armed) _vNorth = _vEast = _vUp = 0;
        _north += _vNorth * dt;
        _east += _vEast * dt;
        _alt = Math.Max(0, _alt + _vUp * dt);

        // Attitude from acceleration (small-angle): pitch forward to accelerate, roll into turns.
        var hs = Math.Sqrt(_vNorth * _vNorth + _vEast * _vEast);
        if (hs > 0.5) _heading = Math.Atan2(_vEast, _vNorth);
        var fwd = accN * Math.Cos(_heading) + accE * Math.Sin(_heading);
        var side = -accN * Math.Sin(_heading) + accE * Math.Cos(_heading);
        if (Phase == SimulatedFlightPhase.Mission) side += hs * hs / Math.Max(10, Param("SURVEY_RADIUS"));
        _pitch = (float)Math.Clamp(-Math.Atan(fwd / 9.81), -0.6, 0.6) + Noise(0.004f);
        _roll = (float)Math.Clamp(Math.Atan(side / 9.81), -0.6, 0.6) + Noise(0.004f);

        // Battery: ~ 18 A hovering, more when climbing.
        var flying = Phase is not (SimulatedFlightPhase.Disarmed or SimulatedFlightPhase.Armed);
        var amps = flying ? 16 + 6 * Math.Abs(_vUp) + hs : Phase == SimulatedFlightPhase.Armed ? 2 : 0.4;
        _consumedMah += amps * 1000 * dt / 3600;
        _battery = Math.Max(13.2, _battery - amps * dt * 0.00022) + Noise(0.002f);
        CurrentAmps = amps;

        if (Phase == SimulatedFlightPhase.Landing && _alt <= 0.05)
        {
            Phase = SimulatedFlightPhase.Disarmed;
            return "Land complete, disarmed";
        }
        return null;
    }

    private double CurrentAmps { get; set; }

    private float Noise(float scale)
    {
        lock (_random) return (float)(_random.NextDouble() - 0.5) * scale;
    }

    private uint BootMs => (uint)(DateTime.UtcNow - _boot).TotalMilliseconds;

    private (int Lat, int Lon) LatLon()
    {
        var lat = HomeLatitude + _north / EarthRadius * 180 / Math.PI;
        var lon = HomeLongitude + _east / (EarthRadius * Math.Cos(HomeLatitude * Math.PI / 180)) * 180 / Math.PI;
        return ((int)Math.Round(lat * 1e7), (int)Math.Round(lon * 1e7));
    }

    private int HeadingCdeg => (int)((_heading * 180 / Math.PI + 360) % 360 * 100);

    private Attitude AttitudeMessage() => new() { TimeBootMs = BootMs, Roll = _roll, Pitch = _pitch, Yaw = (float)_heading, Yawspeed = 0 };

    private GlobalPositionInt PositionMessage()
    {
        var (lat, lon) = LatLon();
        return new GlobalPositionInt
        {
            TimeBootMs = BootMs, Lat = lat, Lon = lon, Alt = (int)((HomeAltitude + _alt) * 1000), RelativeAlt = (int)(_alt * 1000),
            Vx = (short)(_vNorth * 100), Vy = (short)(_vEast * 100), Vz = (short)(-_vUp * 100), Hdg = (ushort)HeadingCdeg,
        };
    }

    private VfrHud HudMessage()
    {
        var gs = Math.Sqrt(_vNorth * _vNorth + _vEast * _vEast);
        var flying = Phase is not (SimulatedFlightPhase.Disarmed or SimulatedFlightPhase.Armed);
        return new VfrHud
        {
            Airspeed = (float)gs, Groundspeed = (float)gs, Heading = (short)(HeadingCdeg / 100), Alt = (float)(HomeAltitude + _alt), Climb = (float)_vUp,
            Throttle = (ushort)(flying ? Math.Clamp(48 + _vUp * 8 + gs, 20, 100) : Phase == SimulatedFlightPhase.Armed ? 12 : 0),
        };
    }

    private GpsRawInt GpsMessage()
    {
        var (lat, lon) = LatLon();
        return new GpsRawInt
        {
            TimeUsec = (ulong)BootMs * 1000, FixType = GpsFixType._3dFix, Lat = lat, Lon = lon, Alt = (int)((HomeAltitude + _alt) * 1000),
            Eph = 90, Epv = 140, Vel = (ushort)(Math.Sqrt(_vNorth * _vNorth + _vEast * _vEast) * 100), Cog = (ushort)HeadingCdeg, SatellitesVisible = 14,
        };
    }

    private int Remaining => (int)Math.Clamp((_battery - 14.0) / (16.8 - 14.0) * 100, 0, 100);

    private SysStatus SystemStatus()
    {
        const MavSysStatusSensor sensors = MavSysStatusSensor._3dGyro | MavSysStatusSensor._3dAccel | MavSysStatusSensor._3dMag
            | MavSysStatusSensor.AbsolutePressure | MavSysStatusSensor.Gps | MavSysStatusSensor.MotorOutputs | MavSysStatusSensor.Battery;
        return new SysStatus
        {
            OnboardControlSensorsPresent = sensors, OnboardControlSensorsEnabled = sensors, OnboardControlSensorsHealth = sensors,
            Load = 380, VoltageBattery = (ushort)(_battery * 1000), CurrentBattery = (short)(CurrentAmps * 100), BatteryRemaining = (sbyte)Remaining,
        };
    }

    private BatteryStatus BatteryMessage()
    {
        var cell = (ushort)(_battery / 4 * 1000);
        var voltages = Enumerable.Repeat(ushort.MaxValue, 10).ToArray();
        for (var i = 0; i < 4; i++) voltages[i] = cell;
        return new BatteryStatus
        {
            Id = 0, BatteryFunction = MavBatteryFunction.All, Type = MavBatteryType.Lipo, Temperature = 3100, Voltages = voltages,
            CurrentBattery = (short)(CurrentAmps * 100), CurrentConsumed = (int)_consumedMah, BatteryRemaining = (sbyte)Remaining,
            EnergyConsumed = -1, TimeRemaining = 0,
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _link.PacketReceived -= OnPacket;
        _link.HeartbeatOverride = null;
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts?.Dispose();
    }
}
