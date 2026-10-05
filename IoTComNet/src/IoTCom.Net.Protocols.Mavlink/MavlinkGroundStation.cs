using System.Collections.Concurrent;
using IoTCom.Net.Protocols.Mavlink.Common;

namespace IoTCom.Net.Protocols.Mavlink;

/// <summary>The latest telemetry of one vehicle, aggregated from its messages.</summary>
public sealed class MavlinkVehicleState
{
    /// <summary>Last heartbeat.</summary>
    public Heartbeat? Heartbeat { get; internal set; }
    /// <summary>Armed (MAV_MODE_FLAG_SAFETY_ARMED).</summary>
    public bool Armed => Heartbeat?.BaseMode.HasFlag(MavModeFlag.SafetyArmed) == true;
    /// <summary>Autopilot-specific flight mode number (custom_mode).</summary>
    public uint CustomMode => Heartbeat?.CustomMode ?? 0;
    /// <summary>System status.</summary>
    public MavState Status => Heartbeat?.SystemStatus ?? MavState.Uninit;
    /// <summary>Roll, rad.</summary>
    public float Roll { get; internal set; }
    /// <summary>Pitch, rad.</summary>
    public float Pitch { get; internal set; }
    /// <summary>Yaw, rad.</summary>
    public float Yaw { get; internal set; }
    /// <summary>Latitude, degrees.</summary>
    public double Latitude { get; internal set; }
    /// <summary>Longitude, degrees.</summary>
    public double Longitude { get; internal set; }
    /// <summary>Altitude above home, m.</summary>
    public double RelativeAltitude { get; internal set; }
    /// <summary>Altitude above MSL, m.</summary>
    public double Altitude { get; internal set; }
    /// <summary>Ground speed, m/s.</summary>
    public float GroundSpeed { get; internal set; }
    /// <summary>Climb rate, m/s.</summary>
    public float Climb { get; internal set; }
    /// <summary>Heading, degrees.</summary>
    public int Heading { get; internal set; }
    /// <summary>Throttle, %.</summary>
    public int Throttle { get; internal set; }
    /// <summary>Battery voltage, V.</summary>
    public double BatteryVoltage { get; internal set; }
    /// <summary>Battery remaining, % (−1 unknown).</summary>
    public int BatteryRemaining { get; internal set; } = -1;
    /// <summary>GPS fix.</summary>
    public GpsFixType GpsFix { get; internal set; }
    /// <summary>Satellites visible.</summary>
    public int Satellites { get; internal set; }
    /// <summary>When telemetry was last received.</summary>
    public DateTimeOffset LastUpdate { get; internal set; }

    internal void Apply(IMavlinkMessage m)
    {
        switch (m)
        {
            case Heartbeat hb: Heartbeat = hb; break;
            case Attitude a: (Roll, Pitch, Yaw) = (a.Roll, a.Pitch, a.Yaw); break;
            case GlobalPositionInt g:
                (Latitude, Longitude, Altitude, RelativeAltitude) = (g.Lat / 1e7, g.Lon / 1e7, g.Alt / 1000.0, g.RelativeAlt / 1000.0);
                break;
            case VfrHud v: (GroundSpeed, Climb, Heading, Throttle) = (v.Groundspeed, v.Climb, v.Heading, v.Throttle); break;
            case SysStatus s: (BatteryVoltage, BatteryRemaining) = (s.VoltageBattery / 1000.0, s.BatteryRemaining); break;
            case GpsRawInt gps: (GpsFix, Satellites) = (gps.FixType, gps.SatellitesVisible); break;
        }
        LastUpdate = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Ground-station helper for one vehicle: COMMAND_LONG with acknowledgement and retransmission (incrementing
/// <c>confirmation</c>), arm/takeoff/land/RTL, the parameter protocol (complete list even on a lossy link), status texts and
/// aggregated telemetry. In read-only mode, commands and parameter writes throw <see cref="ReadOnlyModeException"/>.
/// </summary>
public sealed class MavlinkGroundStation : IDisposable
{
    private readonly MavlinkConnection _link;
    private readonly ConcurrentQueue<string> _texts = new();

    /// <summary>Attaches to <paramref name="link"/> and follows <paramref name="targetSystem"/>.</summary>
    public MavlinkGroundStation(MavlinkConnection link, byte targetSystem = 1, byte targetComponent = 1)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        TargetSystem = targetSystem;
        TargetComponent = targetComponent;
        _link.PacketReceived += OnPacket;
    }

    /// <summary>Vehicle system id.</summary>
    public byte TargetSystem { get; }

    /// <summary>Vehicle component id (1 = autopilot).</summary>
    public byte TargetComponent { get; }

    /// <summary>Blocks commands and parameter writes.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>How long to wait for each COMMAND_ACK / PARAM_VALUE.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Transmissions per command before giving up.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>Aggregated telemetry.</summary>
    public MavlinkVehicleState State { get; } = new();

    /// <summary>Raised for STATUSTEXT messages of the vehicle.</summary>
    public event Action<MavSeverity, string>? StatusText;

    /// <summary>Recent status texts (newest last).</summary>
    public IReadOnlyCollection<string> RecentTexts => [.. _texts];

    private void OnPacket(MavlinkPacket p)
    {
        if (p.SystemId != TargetSystem || p.Message is null) return;
        State.Apply(p.Message);
        if (p.Message is Statustext t)
        {
            _texts.Enqueue(t.Text);
            while (_texts.Count > 20) _texts.TryDequeue(out _);
            StatusText?.Invoke(t.Severity, t.Text);
        }
    }

    /// <summary>Waits until the vehicle's heartbeat has been heard.</summary>
    public async Task<bool> WaitForHeartbeatAsync(TimeSpan timeout, CancellationToken ct = default) =>
        State.Heartbeat is not null || await _link.WaitForAsync<Heartbeat>((_, p) => p.SystemId == TargetSystem, timeout, ct).ConfigureAwait(false) is not null;

    /// <summary>Sends COMMAND_LONG and waits for its COMMAND_ACK (retransmitting with an incremented confirmation).</summary>
    public async Task<MavResult> CommandAsync(MavCmd command, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0,
        CancellationToken ct = default)
    {
        if (ReadOnly) throw new ReadOnlyModeException($"MAV_CMD {command} is blocked: the ground station is in read-only mode.");
        for (byte attempt = 0; attempt < Retries; attempt++)
        {
            var wait = _link.WaitForAsync<CommandAck>((a, p) => a.Command == command && p.SystemId == TargetSystem, Timeout, ct);
            await _link.SendAsync(new CommandLong
            {
                TargetSystem = TargetSystem, TargetComponent = TargetComponent, Command = command, Confirmation = attempt,
                Param1 = p1, Param2 = p2, Param3 = p3, Param4 = p4, Param5 = p5, Param6 = p6, Param7 = p7,
            }, ct).ConfigureAwait(false);
            if (await wait.ConfigureAwait(false) is { } ack)
            {
                if (ack.Message.Result == MavResult.InProgress)
                {
                    // Long-running command: wait for the final result.
                    var final = await _link.WaitForAsync<CommandAck>((a, p) => a.Command == command && a.Result != MavResult.InProgress && p.SystemId == TargetSystem,
                        Timeout * 10, ct).ConfigureAwait(false);
                    return final?.Message.Result ?? MavResult.InProgress;
                }
                return ack.Message.Result;
            }
        }
        throw new IoTComTimeoutException($"No COMMAND_ACK for {command} from system {TargetSystem} after {Retries} attempts.");
    }

    /// <summary>MAV_CMD_COMPONENT_ARM_DISARM (force uses the magic 21196).</summary>
    public Task<MavResult> ArmAsync(bool arm = true, bool force = false, CancellationToken ct = default) =>
        CommandAsync(MavCmd.ComponentArmDisarm, arm ? 1 : 0, force ? 21196 : 0, ct: ct);

    /// <summary>MAV_CMD_NAV_TAKEOFF to <paramref name="altitude"/> metres above home.</summary>
    public Task<MavResult> TakeoffAsync(float altitude, CancellationToken ct = default) => CommandAsync(MavCmd.NavTakeoff, p7: altitude, ct: ct);

    /// <summary>MAV_CMD_NAV_LAND at the current position.</summary>
    public Task<MavResult> LandAsync(CancellationToken ct = default) => CommandAsync(MavCmd.NavLand, ct: ct);

    /// <summary>MAV_CMD_NAV_RETURN_TO_LAUNCH.</summary>
    public Task<MavResult> ReturnToLaunchAsync(CancellationToken ct = default) => CommandAsync(MavCmd.NavReturnToLaunch, ct: ct);

    /// <summary>Reads every parameter (PARAM_REQUEST_LIST, then PARAM_REQUEST_READ for any index that was lost).</summary>
    public async Task<IReadOnlyDictionary<string, float>> ReadParametersAsync(CancellationToken ct = default)
    {
        var got = new ConcurrentDictionary<int, ParamValue>();
        void Collect(MavlinkPacket p)
        {
            if (p.SystemId == TargetSystem && p.Message is ParamValue v) got[v.ParamIndex] = v;
        }
        _link.PacketReceived += Collect;
        try
        {
            await _link.SendAsync(new ParamRequestList { TargetSystem = TargetSystem, TargetComponent = TargetComponent }, ct).ConfigureAwait(false);
            var count = -1;
            var quiet = DateTime.UtcNow;
            var lastSeen = 0;
            for (var round = 0; round < 50; round++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                if (!got.IsEmpty) count = got.Values.First().ParamCount;
                if (count >= 0 && got.Count >= count) break;
                if (got.Count != lastSeen)
                {
                    lastSeen = got.Count;
                    quiet = DateTime.UtcNow;
                    continue;
                }
                if (DateTime.UtcNow - quiet < Timeout / 2) continue;
                // Stream went quiet: ask for the missing ones individually (or the whole list again if we know nothing).
                if (count < 0)
                {
                    await _link.SendAsync(new ParamRequestList { TargetSystem = TargetSystem, TargetComponent = TargetComponent }, ct).ConfigureAwait(false);
                }
                else
                {
                    for (var i = 0; i < count; i++)
                        if (!got.ContainsKey(i))
                            await _link.SendAsync(new ParamRequestRead { TargetSystem = TargetSystem, TargetComponent = TargetComponent, ParamIndex = (short)i }, ct).ConfigureAwait(false);
                }
                quiet = DateTime.UtcNow;
            }
            if (count < 0 || got.Count < count) throw new IoTComTimeoutException($"Parameter download incomplete: {got.Count} of {(count < 0 ? "?" : count)}.");
            return got.Values.OrderBy(v => v.ParamIndex).ToDictionary(v => v.ParamId, v => v.Value, StringComparer.Ordinal);
        }
        finally
        {
            _link.PacketReceived -= Collect;
        }
    }

    /// <summary>PARAM_SET, confirmed by the echoed PARAM_VALUE; returns the value the vehicle now reports.</summary>
    public async Task<float> SetParameterAsync(string name, float value, MavParamType type = MavParamType.Real32, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 16) throw new ArgumentException("Parameter names have at most 16 characters.", nameof(name));
        if (ReadOnly) throw new ReadOnlyModeException($"Writing parameter {name} is blocked: the ground station is in read-only mode.");
        for (var attempt = 0; attempt < Retries; attempt++)
        {
            var wait = _link.WaitForAsync<ParamValue>((v, p) => p.SystemId == TargetSystem && v.ParamId == name, Timeout, ct);
            await _link.SendAsync(new ParamSet { TargetSystem = TargetSystem, TargetComponent = TargetComponent, ParamId = name, ParamValue = value, ParamType = type }, ct)
                .ConfigureAwait(false);
            if (await wait.ConfigureAwait(false) is { } echo) return echo.Message.Value;
        }
        throw new IoTComTimeoutException($"No PARAM_VALUE echo for {name} after {Retries} attempts.");
    }

    /// <inheritdoc />
    public void Dispose() => _link.PacketReceived -= OnPacket;
}
