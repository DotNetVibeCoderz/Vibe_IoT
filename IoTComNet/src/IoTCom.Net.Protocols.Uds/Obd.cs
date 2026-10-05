using System.Text;
using IoTCom.Net.Protocols.IsoTp;
using IoTCom.Net.Transport.Can;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Uds;

/// <summary>An OBD-II mode 01 parameter (SAE J1979): how to decode (and, for simulators, encode) its value.</summary>
/// <param name="Pid">PID number.</param>
/// <param name="Name">English name.</param>
/// <param name="Unit">Unit.</param>
/// <param name="Bytes">Data bytes.</param>
/// <param name="Min">Smallest value.</param>
/// <param name="Max">Largest value.</param>
public sealed record ObdPid(byte Pid, string Name, string Unit, int Bytes, double Min, double Max)
{
    /// <summary>Decodes the data bytes (A, B, …) of a response.</summary>
    public double Decode(ReadOnlySpan<byte> d) => Pid switch
    {
        0x04 or 0x11 or 0x2F => d[0] * 100.0 / 255,
        0x05 or 0x0F or 0x46 or 0x5C => d[0] - 40,
        0x0B or 0x0D => d[0],
        0x0C => (d[0] * 256 + d[1]) / 4.0,
        0x10 => (d[0] * 256 + d[1]) / 100.0,
        0x1F => d[0] * 256 + d[1],
        0x42 => (d[0] * 256 + d[1]) / 1000.0,
        0x5E => (d[0] * 256 + d[1]) / 20.0,
        _ => Bytes == 1 ? d[0] : d[0] * 256 + d[1],
    };

    /// <summary>Encodes a physical value into data bytes (clamped to the range).</summary>
    public byte[] Encode(double value)
    {
        value = Math.Clamp(value, Min, Max);
        int raw = Pid switch
        {
            0x04 or 0x11 or 0x2F => (int)Math.Round(value * 255 / 100),
            0x05 or 0x0F or 0x46 or 0x5C => (int)Math.Round(value + 40),
            0x0C => (int)Math.Round(value * 4),
            0x10 => (int)Math.Round(value * 100),
            0x42 => (int)Math.Round(value * 1000),
            0x5E => (int)Math.Round(value * 20),
            _ => (int)Math.Round(value),
        };
        return Bytes == 1 ? [(byte)Math.Clamp(raw, 0, 255)] : [(byte)(Math.Clamp(raw, 0, 65535) >> 8), (byte)Math.Clamp(raw, 0, 65535)];
    }
}

/// <summary>A decoded OBD-II value.</summary>
/// <param name="Pid">The parameter.</param>
/// <param name="Value">Physical value.</param>
/// <param name="Raw">Raw data bytes.</param>
public readonly record struct ObdValue(ObdPid Pid, double Value, byte[] Raw)
{
    /// <inheritdoc />
    public override string ToString() => $"{Pid.Name}: {Value:0.##} {Pid.Unit}";
}

/// <summary>The SAE J1979 mode 01 PIDs IoTCom.Net decodes.</summary>
public static class ObdPids
{
    /// <summary>0x04 calculated engine load.</summary>
    public static readonly ObdPid EngineLoad = new(0x04, "Engine load", "%", 1, 0, 100);
    /// <summary>0x05 engine coolant temperature.</summary>
    public static readonly ObdPid CoolantTemperature = new(0x05, "Coolant temperature", "°C", 1, -40, 215);
    /// <summary>0x0B intake manifold absolute pressure.</summary>
    public static readonly ObdPid IntakePressure = new(0x0B, "Intake manifold pressure", "kPa", 1, 0, 255);
    /// <summary>0x0C engine speed.</summary>
    public static readonly ObdPid EngineRpm = new(0x0C, "Engine speed", "rpm", 2, 0, 16383.75);
    /// <summary>0x0D vehicle speed.</summary>
    public static readonly ObdPid VehicleSpeed = new(0x0D, "Vehicle speed", "km/h", 1, 0, 255);
    /// <summary>0x0F intake air temperature.</summary>
    public static readonly ObdPid IntakeTemperature = new(0x0F, "Intake air temperature", "°C", 1, -40, 215);
    /// <summary>0x10 mass air flow.</summary>
    public static readonly ObdPid MassAirFlow = new(0x10, "Mass air flow", "g/s", 2, 0, 655.35);
    /// <summary>0x11 throttle position.</summary>
    public static readonly ObdPid ThrottlePosition = new(0x11, "Throttle position", "%", 1, 0, 100);
    /// <summary>0x1F run time since engine start.</summary>
    public static readonly ObdPid RunTime = new(0x1F, "Run time since start", "s", 2, 0, 65535);
    /// <summary>0x2F fuel tank level.</summary>
    public static readonly ObdPid FuelLevel = new(0x2F, "Fuel level", "%", 1, 0, 100);
    /// <summary>0x42 control module voltage.</summary>
    public static readonly ObdPid ModuleVoltage = new(0x42, "Control module voltage", "V", 2, 0, 65.535);
    /// <summary>0x46 ambient air temperature.</summary>
    public static readonly ObdPid AmbientTemperature = new(0x46, "Ambient air temperature", "°C", 1, -40, 215);
    /// <summary>0x5C engine oil temperature.</summary>
    public static readonly ObdPid OilTemperature = new(0x5C, "Engine oil temperature", "°C", 1, -40, 210);
    /// <summary>0x5E engine fuel rate.</summary>
    public static readonly ObdPid FuelRate = new(0x5E, "Engine fuel rate", "L/h", 2, 0, 3276.75);

    /// <summary>All known PIDs by number.</summary>
    public static IReadOnlyDictionary<byte, ObdPid> All { get; } = new[]
    {
        EngineLoad, CoolantTemperature, IntakePressure, EngineRpm, VehicleSpeed, IntakeTemperature, MassAirFlow,
        ThrottlePosition, RunTime, FuelLevel, ModuleVoltage, AmbientTemperature, OilTemperature, FuelRate,
    }.ToDictionary(p => p.Pid);
}

/// <summary>OBD-II scan tool options.</summary>
public sealed class ObdClientOptions
{
    /// <summary>Functional request identifier (0x7DF, all emission ECUs).</summary>
    public uint FunctionalId { get; set; } = 0x7DF;

    /// <summary>Physical identifier of the ECU we follow (flow control goes here), 0x7E0 = engine.</summary>
    public uint EcuRequestId { get; set; } = 0x7E0;

    /// <summary>Response identifier of that ECU (0x7E8 = engine).</summary>
    public uint ResponseId { get; set; } = 0x7E8;

    /// <summary>Response timeout (J1979 allows 50 ms; real vehicles often need more).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Blocks mode 04 (clear DTCs and freeze frames).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// OBD-II (SAE J1979 over ISO 15765-4) scan tool: live data (mode 01), stored/pending DTCs (03/07), clear (04) and
/// vehicle information (09). Requests go out functionally on 0x7DF; the answer of one ECU (0x7E8 by default) is
/// reassembled with ISO-TP.
/// </summary>
public sealed class ObdClient : EndpointBase, IClientEndpoint
{
    private readonly ICanBus _bus;
    private readonly IsoTpChannel _channel;
    private readonly ObdClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ObdClient(ICanBus bus, ObdClientOptions options) : base("obd2", options.Logger)
    {
        _bus = bus;
        _options = options;
        _channel = IsoTpChannel.Create(bus, o =>
        {
            o.TxId = options.EcuRequestId;
            o.RxId = options.ResponseId;
            o.Logger = options.Logger;
        });
        Name = $"OBD {options.ResponseId:X3}";
    }

    /// <summary>Creates a scan tool on <paramref name="bus"/>.</summary>
    public static ObdClient Create(ICanBus bus, Action<ObdClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var options = new ObdClientOptions();
        configure?.Invoke(options);
        return new ObdClient(bus, options);
    }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        await _channel.ConnectAsync(ct).ConfigureAwait(false);
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        await _channel.DisconnectAsync(ct).ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <summary>Sends a functional request (mode + parameters, at most 7 bytes) and returns the positive response.</summary>
    public async Task<byte[]> QueryAsync(byte mode, ReadOnlyMemory<byte> parameters = default, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (parameters.Length > 6) throw new ArgumentException("OBD requests carry at most 6 parameter bytes.", nameof(parameters));
        if (_options.ReadOnly && mode == 0x04) throw new ReadOnlyModeException("OBD mode 04 (clear DTCs) is blocked: the client is in read-only mode.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var frame = new byte[8];
            Array.Fill(frame, (byte)0xCC);
            frame[0] = (byte)(1 + parameters.Length);
            frame[1] = mode;
            parameters.Span.CopyTo(frame.AsSpan(2));
            _channel.DiscardPending();
            var request = frame.AsSpan(1, 1 + parameters.Length).ToArray();
            Tap(FrameDirection.Outbound, request, () => $"mode {mode:X2}");
            await _bus.SendAsync(new CanFrame(_options.FunctionalId, frame, _options.FunctionalId > CanFrame.MaxStandardId ? CanFrameFlags.Extended : CanFrameFlags.None), ct).ConfigureAwait(false);
            var timeout = _options.Timeout;
            while (true)
            {
                var r = await _channel.ReceiveAsync(timeout, ct).ConfigureAwait(false)
                        ?? throw new IoTComTimeoutException($"No OBD response to mode {mode:X2} within {timeout.TotalMilliseconds:0} ms (ignition on? correct bit rate?).");
                Tap(FrameDirection.Inbound, r, () => $"mode {mode:X2} response");
                if (r.Length >= 3 && r[0] == UdsService.NegativeResponse && r[1] == mode)
                {
                    if ((UdsNrc)r[2] == UdsNrc.ResponsePending)
                    {
                        timeout = TimeSpan.FromSeconds(5);
                        continue;
                    }
                    throw new UdsNegativeResponseException(mode, (UdsNrc)r[2]);
                }
                if (r.Length >= 1 && r[0] == mode + 0x40) return r;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Mode 01: reads one PID.</summary>
    public async Task<ObdValue> ReadPidAsync(ObdPid pid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pid);
        var r = await QueryAsync(0x01, new byte[] { pid.Pid }, ct).ConfigureAwait(false);
        if (r.Length < 2 + pid.Bytes || r[1] != pid.Pid) throw new ProtocolException($"Malformed response to PID 0x{pid.Pid:X2}.");
        var raw = r.AsSpan(2, pid.Bytes).ToArray();
        return new ObdValue(pid, pid.Decode(raw), raw);
    }

    /// <summary>Mode 01: PIDs supported by the ECU (from PIDs 0x00, 0x20, 0x40 …).</summary>
    public async Task<IReadOnlyList<byte>> GetSupportedPidsAsync(CancellationToken ct = default)
    {
        var result = new List<byte>();
        for (var basePid = 0; basePid < 0xE0; basePid += 0x20)
        {
            var r = await QueryAsync(0x01, new[] { (byte)basePid }, ct).ConfigureAwait(false);
            if (r.Length < 6) break;
            var mask = (uint)(r[2] << 24 | r[3] << 16 | r[4] << 8 | r[5]);
            for (var bit = 0; bit < 32; bit++)
                if ((mask & (0x8000_0000u >> bit)) != 0) result.Add((byte)(basePid + bit + 1));
            if ((mask & 1) == 0) break;
        }
        result.RemoveAll(p => p % 0x20 == 0);
        return result;
    }

    /// <summary>Mode 09 PID 02: VIN.</summary>
    public async Task<string> ReadVinAsync(CancellationToken ct = default)
    {
        var r = await QueryAsync(0x09, new byte[] { 0x02 }, ct).ConfigureAwait(false);
        // 49 02 <count> <17 ASCII chars> (ISO 15765-4); some ECUs omit the count byte.
        var text = r.Length >= 20 ? r.AsSpan(r.Length - 17) : r.AsSpan(Math.Min(3, r.Length));
        return Encoding.ASCII.GetString(text).Trim('\0', ' ');
    }

    /// <summary>Mode 03: stored (confirmed) DTCs.</summary>
    public Task<IReadOnlyList<Dtc>> ReadDtcsAsync(CancellationToken ct = default) => ReadDtcModeAsync(0x03, DtcStatus.Confirmed, ct);

    /// <summary>Mode 07: pending DTCs (current or last driving cycle).</summary>
    public Task<IReadOnlyList<Dtc>> ReadPendingDtcsAsync(CancellationToken ct = default) => ReadDtcModeAsync(0x07, DtcStatus.Pending, ct);

    private async Task<IReadOnlyList<Dtc>> ReadDtcModeAsync(byte mode, DtcStatus status, CancellationToken ct)
    {
        var r = await QueryAsync(mode, default, ct).ConfigureAwait(false);
        var list = new List<Dtc>();
        if (r.Length < 2) return list;
        var count = r[1];
        for (var i = 0; i < count && 2 + i * 2 + 1 < r.Length; i++)
        {
            var code = (ushort)(r[2 + i * 2] << 8 | r[3 + i * 2]);
            if (code != 0) list.Add(Dtc.FromObd(code, status));
        }
        return list;
    }

    /// <summary>Mode 04: clears DTCs and freeze-frame data, turns the MIL off (write service).</summary>
    public Task ClearDtcsAsync(CancellationToken ct = default) => QueryAsync(0x04, default, ct);

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
