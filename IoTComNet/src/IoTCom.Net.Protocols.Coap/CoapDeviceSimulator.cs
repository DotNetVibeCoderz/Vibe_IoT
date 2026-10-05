using System.Globalization;
using System.Text;
using IoTCom.Net.Serialization.SenML;

namespace IoTCom.Net.Protocols.Coap;

/// <summary>
/// A simulated constrained device — a greenhouse node — on a <see cref="CoapServer"/>. Its resources cover the
/// interesting parts of CoAP: observable sensors (text or SenML by Accept), actuators with PUT, a slow routine that
/// triggers a separate response, a 3 KB log served with Block2 and a firmware upload accepted with Block1.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>/sensors/temperature, /sensors/humidity, /sensors/soil</term><description>GET, observable; text/plain, or SenML JSON/CBOR with Accept 110/112</description></item>
/// <item><term>/actuators/fan</term><description>GET, PUT "on"/"off" (cools the air)</description></item>
/// <item><term>/actuators/valve</term><description>GET, PUT 0–100 (% open, wets the soil)</description></item>
/// <item><term>/device/info</term><description>GET JSON</description></item>
/// <item><term>/device/log</term><description>GET ≈ 3 KB text (Block2)</description></item>
/// <item><term>/device/calibrate</term><description>POST, takes 1.5 s (separate response)</description></item>
/// <item><term>/firmware</term><description>PUT any size (Block1); GET returns the size and checksum received</description></item>
/// </list>
/// </remarks>
public sealed class CoapDeviceSimulator : IAsyncDisposable
{
    private readonly CoapServer _server;
    private readonly Random _random;
    private readonly Lock _gate = new();
    private readonly CoapResource _temperature, _humidity, _soil;
    private readonly List<string> _log = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private double _lastTemp = double.NaN, _lastHum = double.NaN, _lastSoil = double.NaN;
    private int _ticks;
    private byte[] _firmware = [];

    /// <summary>Creates the device's resources on <paramref name="server"/>.</summary>
    public CoapDeviceSimulator(CoapServer server, string name = "greenhouse-node-01", int seed = 3)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        Name = name;
        _random = new Random(seed);
        _temperature = server.Map("/sensors/temperature", get: (r, _) => Sensor(r, "temperature", Temperature, "Cel"), observable: true,
            resourceType: "temperature-c", title: "Air temperature", contentFormat: CoapContentFormat.TextPlain);
        _humidity = server.Map("/sensors/humidity", get: (r, _) => Sensor(r, "humidity", Humidity, "%RH"), observable: true,
            resourceType: "humidity-rh", title: "Relative humidity", contentFormat: CoapContentFormat.TextPlain);
        _soil = server.Map("/sensors/soil", get: (r, _) => Sensor(r, "soil", SoilMoisture, "%"), observable: true,
            resourceType: "soil-moisture", title: "Soil moisture", contentFormat: CoapContentFormat.TextPlain);
        server.Map("/actuators/fan", get: (_, _) => Text(FanOn ? "on" : "off"), put: (r, _) => SetFan(r), resourceType: "switch", title: "Ventilation fan");
        server.Map("/actuators/valve", get: (_, _) => Text(ValvePercent.ToString(CultureInfo.InvariantCulture)), put: (r, _) => SetValve(r), resourceType: "valve-percent", title: "Irrigation valve");
        server.Map("/device/info", get: (_, _) => ValueTask.FromResult(CoapReply.Content(
            $"{{\"name\":\"{Name}\",\"model\":\"IoTCom sim\",\"fw\":\"2.1.0\",\"uptime\":{_ticks}}}", CoapContentFormat.Json)), title: "Device information", contentFormat: CoapContentFormat.Json);
        server.Map("/device/log", get: (_, _) => Text(LogText()), title: "Event log (Block2)");
        server.Map("/device/calibrate", post: async (_, ct) =>
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            AddLog("calibration done");
            return CoapReply.Content("calibrated");
        }, title: "Slow routine (separate response)");
        server.Map("/firmware", get: (_, _) => Text($"{_firmware.Length} bytes, sum {_firmware.Aggregate(0u, (a, b) => a + b)}"),
            put: (r, _) =>
            {
                _firmware = r.Payload.ToArray();
                AddLog($"firmware image received: {_firmware.Length} bytes");
                return ValueTask.FromResult(CoapReply.Changed());
            }, title: "Firmware upload (Block1)");
        for (var i = 0; i < 60; i++) AddLog($"boot step {i:00}: subsystem {(char)('A' + i % 26)} ready, self-test passed, heap {32 - i % 7} KiB free");
    }

    /// <summary>Device name.</summary>
    public string Name { get; }

    /// <summary>Air temperature, °C.</summary>
    public double Temperature { get; private set; } = 27.5;

    /// <summary>Relative humidity, %.</summary>
    public double Humidity { get; private set; } = 68;

    /// <summary>Soil moisture, %.</summary>
    public double SoilMoisture { get; private set; } = 41;

    /// <summary>Fan state.</summary>
    public bool FanOn { get; private set; }

    /// <summary>Valve opening, %.</summary>
    public int ValvePercent { get; private set; }

    /// <summary>Starts the physics (1 s tick) and the notifications.</summary>
    public void Start(TimeSpan? tick = null)
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var period = tick ?? TimeSpan.FromSeconds(1);
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await StepAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    /// <summary>Advances the physics one step and notifies observers of changed sensors.</summary>
    public async Task StepAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _ticks++;
            var sun = 30 + 4 * Math.Sin(_ticks / 40.0);
            Temperature += (sun - (FanOn ? 4 : 0) - Temperature) * 0.08 + (_random.NextDouble() - 0.5) * 0.15;
            Humidity += ((FanOn ? 55 : 72) - Humidity) * 0.05 + (_random.NextDouble() - 0.5) * 0.4;
            SoilMoisture += (ValvePercent / 100.0 * 2.2) - 0.35 + (_random.NextDouble() - 0.5) * 0.1;
            SoilMoisture = Math.Clamp(SoilMoisture, 5, 95);
        }
        // Notify on a meaningful change, or at least every 10 ticks (keeps observers fresh, RFC 7641 Max-Age).
        if (Changed(ref _lastTemp, Temperature, 0.1)) await _temperature.NotifyAsync(ct).ConfigureAwait(false);
        if (Changed(ref _lastHum, Humidity, 0.5)) await _humidity.NotifyAsync(ct).ConfigureAwait(false);
        if (Changed(ref _lastSoil, SoilMoisture, 0.5)) await _soil.NotifyAsync(ct).ConfigureAwait(false);
    }

    private bool Changed(ref double last, double value, double step)
    {
        if (!double.IsNaN(last) && Math.Abs(value - last) < step && _ticks % 10 != 0) return false;
        last = value;
        return true;
    }

    private static ValueTask<CoapReply> Text(string text) => ValueTask.FromResult(CoapReply.Content(text));

    private ValueTask<CoapReply> Sensor(CoapRequest r, string name, double value, string unit)
    {
        value = Math.Round(value, 2);
        return ValueTask.FromResult(r.Accept switch
        {
            null or CoapContentFormat.TextPlain => CoapReply.Content(value.ToString("0.0#", CultureInfo.InvariantCulture)) with { MaxAge = 30 },
            CoapContentFormat.SenMLJson => CoapReply.Content(SenMLCodec.ToJson(Pack(name, value, unit)), CoapContentFormat.SenMLJson) with { MaxAge = 30 },
            CoapContentFormat.SenMLCbor => CoapReply.Content(SenMLCodec.ToCbor(Pack(name, value, unit)), CoapContentFormat.SenMLCbor) with { MaxAge = 30 },
            _ => CoapReply.Error(CoapCode.NotAcceptable, "Accept 0, 110 or 112"),
        });
    }

    private IReadOnlyList<SenMLRecord> Pack(string name, double value, string unit) =>
        new SenMLPackBuilder($"urn:dev:{Name}:").At(DateTimeOffset.UtcNow).Add(name, value, unit).Build();

    private ValueTask<CoapReply> SetFan(CoapRequest r)
    {
        var v = r.PayloadText.Trim().ToLowerInvariant();
        if (v is not ("on" or "off" or "1" or "0" or "true" or "false")) return ValueTask.FromResult(CoapReply.Error(CoapCode.BadRequest, "on or off"));
        FanOn = v is "on" or "1" or "true";
        AddLog($"fan {(FanOn ? "on" : "off")} by {r.Remote}");
        return ValueTask.FromResult(CoapReply.Changed());
    }

    private ValueTask<CoapReply> SetValve(CoapRequest r)
    {
        if (!int.TryParse(r.PayloadText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v is < 0 or > 100)
            return ValueTask.FromResult(CoapReply.Error(CoapCode.BadRequest, "0-100"));
        ValvePercent = v;
        AddLog($"valve {v}% by {r.Remote}");
        return ValueTask.FromResult(CoapReply.Changed());
    }

    private void AddLog(string line)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.UtcNow:HH:mm:ss} {line}");
            if (_log.Count > 200) _log.RemoveAt(0);
        }
    }

    private string LogText()
    {
        lock (_log) return string.Join('\n', _log.TakeLast(60));
    }

    /// <summary>Encodes text as UTF-8 (helper for samples).</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts?.Dispose();
    }
}
