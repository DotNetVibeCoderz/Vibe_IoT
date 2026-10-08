using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace IoTCom.Net.Protocols.LoRaWan;

/// <summary>What a simulated device measures and how it encodes it.</summary>
public enum LoRaWanSensorKind
{
    /// <summary>Temperature, humidity and battery (Cayenne LPP, FPort 1).</summary>
    Environment,
    /// <summary>Soil moisture, soil temperature and battery (Cayenne LPP, FPort 1).</summary>
    SoilMoisture,
    /// <summary>Cumulative litres and a leak flag (4-byte counter + flags, FPort 2).</summary>
    WaterMeter,
    /// <summary>A moving GPS tracker with battery (Cayenne LPP, FPort 1).</summary>
    GpsTracker,
}

/// <summary>A simulated gateway.</summary>
/// <param name="Eui">Gateway EUI.</param>
/// <param name="Name">Name.</param>
/// <param name="X">East position in km.</param>
/// <param name="Y">North position in km.</param>
public sealed record LoRaWanSimulatedGateway(Eui64 Eui, string Name, double X, double Y);

/// <summary>A simulated end device.</summary>
public sealed record LoRaWanSimulatedDeviceOptions
{
    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Device EUI.</summary>
    public required Eui64 DevEui { get; init; }

    /// <summary>AppKey (OTAA).</summary>
    public required byte[] AppKey { get; init; }

    /// <summary>Join EUI.</summary>
    public Eui64 JoinEui { get; init; }

    /// <summary>East position in km.</summary>
    public double X { get; init; }

    /// <summary>North position in km.</summary>
    public double Y { get; init; }

    /// <summary>Sensor.</summary>
    public LoRaWanSensorKind Sensor { get; init; } = LoRaWanSensorKind.Environment;

    /// <summary>Reporting interval (FPort 10 downlinks change it).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Share of uplinks sent confirmed (0..1).</summary>
    public double ConfirmedRatio { get; init; } = 0.2;

    /// <summary>Fixed data rate index; null picks the fastest rate the link supports.</summary>
    public int? DataRate { get; init; }
}

/// <summary>A radio transmission seen by the simulator.</summary>
public sealed record LoRaWanRadioEvent
{
    /// <summary>Time.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Device name (or null for a downlink no device accepted).</summary>
    public string? Device { get; init; }

    /// <summary>Uplink (device → gateways) or downlink.</summary>
    public bool Uplink { get; init; }

    /// <summary>The PHYPayload.</summary>
    public byte[] Phy { get; init; } = [];

    /// <summary>Frequency in MHz.</summary>
    public double Frequency { get; init; }

    /// <summary>Data rate.</summary>
    public string DataRate { get; init; } = "";

    /// <summary>Time on air.</summary>
    public TimeSpan Airtime { get; init; }

    /// <summary>Gateways that heard an uplink (name, RSSI, SNR); empty when it was lost.</summary>
    public IReadOnlyList<(string Gateway, double Rssi, double Snr)> Receptions { get; init; } = [];

    /// <summary>Gateway that transmitted a downlink.</summary>
    public string? Gateway { get; init; }

    /// <summary>Human-readable summary (decoded application payload where possible).</summary>
    public string Summary { get; init; } = "";
}

/// <summary>A device in the simulation.</summary>
public sealed class LoRaWanSimulatedDevice
{
    internal LoRaWanSimulatedDevice(LoRaWanSimulatedDeviceOptions options)
    {
        Options = options;
        Mac = new LoRaWanEndDevice(options.DevEui, options.JoinEui, options.AppKey) { Battery = 230 };
        Interval = options.Interval;
        X = options.X;
        Y = options.Y;
    }

    internal TaskCompletionSource? JoinWaiter;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Wake() => Volatile.Read(ref _wake).TrySetResult();

    internal async Task SleepAsync(TimeSpan duration, CancellationToken ct)
    {
        await Task.WhenAny(Volatile.Read(ref _wake).Task, Task.Delay(duration, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        Volatile.Write(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>Options.</summary>
    public LoRaWanSimulatedDeviceOptions Options { get; }

    /// <summary>Name.</summary>
    public string Name => Options.Name;

    /// <summary>The device MAC (session, counters, data rate).</summary>
    public LoRaWanEndDevice Mac { get; }

    /// <summary>Current reporting interval.</summary>
    public TimeSpan Interval { get; internal set; }

    /// <summary>East position in km (trackers move).</summary>
    public double X { get; internal set; }

    /// <summary>North position in km.</summary>
    public double Y { get; internal set; }

    /// <summary>Uplinks sent.</summary>
    public int Uplinks { get; internal set; }

    /// <summary>Uplinks no gateway heard.</summary>
    public int Lost { get; internal set; }

    /// <summary>Downlinks received.</summary>
    public int Downlinks { get; internal set; }

    /// <summary>Joins.</summary>
    public int Joins { get; internal set; }

    /// <summary>Last measurement, decoded.</summary>
    public string LastReading { get; internal set; } = "";

    internal double Temperature = 27, Humidity = 70, Soil = 45, Litres = 12_000, Volts = 3.6, Heading;
}

/// <summary>Simulator options.</summary>
public sealed class LoRaWanSimulatorOptions
{
    /// <summary>Network server address (Semtech UDP).</summary>
    public EndPoint Server { get; set; } = new IPEndPoint(IPAddress.Loopback, 1700);

    /// <summary>Binds each gateway's transport (default UDP, ephemeral port).</summary>
    public DatagramTransportFactory? GatewayTransportFactory { get; set; }

    /// <summary>Regional parameters.</summary>
    public LoRaRegion Region { get; set; } = LoRaRegion.EU868;

    /// <summary>Gateways.</summary>
    public IList<LoRaWanSimulatedGateway> Gateways { get; } = [];

    /// <summary>Devices.</summary>
    public IList<LoRaWanSimulatedDeviceOptions> Devices { get; } = [];

    /// <summary>Gateways transmit downlinks at the requested timestamp (real RX1 timing). False delivers at once.</summary>
    public bool HonorTimestamps { get; set; } = true;

    /// <summary>How long a device waits for a Join-Accept before retrying.</summary>
    public TimeSpan JoinTimeout { get; set; } = TimeSpan.FromSeconds(7);

    /// <summary>Log-normal shadowing (standard deviation in dB).</summary>
    public double ShadowingDb { get; set; } = 3;

    /// <summary>Gateway keep-alive interval.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Random seed.</summary>
    public int Seed { get; set; } = 7;

    /// <summary>A small city deployment: two gateways and four devices of different kinds (AppKeys are random).</summary>
    public static LoRaWanSimulatorOptions Demo(EndPoint server, LoRaRegion? region = null)
    {
        var o = new LoRaWanSimulatorOptions { Server = server, Region = region ?? LoRaRegion.AS923Group2 };
        o.Gateways.Add(new LoRaWanSimulatedGateway(new Eui64(0xAA555A0000000001), "gw-rooftop", 0, 0));
        o.Gateways.Add(new LoRaWanSimulatedGateway(new Eui64(0xAA555A0000000002), "gw-tower", 4.0, 1.5));
        o.Devices.Add(new() { Name = "weather-01", DevEui = new Eui64(0x70B3D57ED0000101), AppKey = LoRaWanKeys.Random(), X = 0.8, Y = 0.6, Sensor = LoRaWanSensorKind.Environment, Interval = TimeSpan.FromSeconds(20) });
        o.Devices.Add(new() { Name = "soil-07", DevEui = new Eui64(0x70B3D57ED0000107), AppKey = LoRaWanKeys.Random(), X = 2.6, Y = -1.2, Sensor = LoRaWanSensorKind.SoilMoisture, Interval = TimeSpan.FromSeconds(25) });
        o.Devices.Add(new() { Name = "water-12", DevEui = new Eui64(0x70B3D57ED0000112), AppKey = LoRaWanKeys.Random(), X = 5.5, Y = 2.4, Sensor = LoRaWanSensorKind.WaterMeter, Interval = TimeSpan.FromSeconds(30), ConfirmedRatio = 0.5 });
        o.Devices.Add(new() { Name = "tracker-03", DevEui = new Eui64(0x70B3D57ED0000103), AppKey = LoRaWanKeys.Random(), X = 1.5, Y = 1.0, Sensor = LoRaWanSensorKind.GpsTracker, Interval = TimeSpan.FromSeconds(15), ConfirmedRatio = 0 });
        return o;
    }
}

/// <summary>
/// A LoRaWAN deployment in a box: virtual gateways (real <see cref="SemtechPacketForwarder"/>s speaking Semtech UDP to
/// a network server) and Class A devices that join over the air and report sensor data. A log-distance path-loss
/// model with shadowing decides which gateways hear each uplink and with what RSSI/SNR; data rates adapt to the link.
/// Downlinks honour the RX1 timestamp. Point it at <see cref="LoRaWanNetworkServer"/>, or at ChirpStack / TTS.
/// </summary>
public sealed class LoRaWanSimulator : IAsyncDisposable
{
    private const double BaseLatitude = -6.2000, BaseLongitude = 106.8166;
    private readonly LoRaWanSimulatorOptions _options;
    private readonly List<(LoRaWanSimulatedGateway Info, SemtechPacketForwarder Forwarder)> _gateways = [];
    private readonly List<LoRaWanSimulatedDevice> _devices;
    private readonly Random _random;
    private readonly Lock _randomGate = new();
    private CancellationTokenSource? _cts;
    private Task? _loops;

    /// <summary>Creates the simulation (call <see cref="StartAsync"/>).</summary>
    public LoRaWanSimulator(LoRaWanSimulatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Gateways.Count == 0) throw new ArgumentException("Add at least one gateway.", nameof(options));
        _options = options;
        _random = new Random(options.Seed);
        _devices = [.. options.Devices.Select(d => new LoRaWanSimulatedDevice(d))];
    }

    /// <summary>Every uplink and downlink on the air.</summary>
    public event EventHandler<LoRaWanRadioEvent>? RadioActivity;

    /// <summary>Devices.</summary>
    public IReadOnlyList<LoRaWanSimulatedDevice> Devices => _devices;

    /// <summary>Gateways and their forwarders (attach taps here).</summary>
    public IReadOnlyList<(LoRaWanSimulatedGateway Info, SemtechPacketForwarder Forwarder)> Gateways => _gateways;

    /// <summary>Region.</summary>
    public LoRaRegion Region => _options.Region;

    /// <summary>The registrations to add to a network server (DevEUI and AppKey of every device).</summary>
    public IEnumerable<LoRaWanDeviceRegistration> Registrations =>
        _devices.Select(d => LoRaWanDeviceRegistration.Otaa(d.Options.DevEui, d.Options.AppKey, d.Name));

    /// <summary>Connects the gateways and starts the devices.</summary>
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return;
        foreach (var g in _options.Gateways)
        {
            var forwarder = SemtechPacketForwarder.Create(o =>
            {
                o.GatewayEui = g.Eui;
                o.Name = g.Name;
                o.Server = _options.Server;
                o.KeepAliveInterval = _options.KeepAliveInterval;
                o.Position = (BaseLatitude + (g.Y / 111.0), BaseLongitude + (g.X / 110.0), 40);
                if (_options.GatewayTransportFactory is { } f) o.TransportFactory = f;
            });
            var info = g;
            forwarder.TransmitRequested += tx => TransmitDownlinkAsync(info, forwarder, tx);
            await forwarder.ConnectAsync(ct).ConfigureAwait(false);
            _gateways.Add((g, forwarder));
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loops = Task.WhenAll(_devices.Select((d, i) => Task.Run(() => DeviceLoopAsync(d, i, token))));
    }

    /// <summary>Stops devices and gateways.</summary>
    public async ValueTask StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loops!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var (_, f) in _gateways) await f.DisposeAsync().ConfigureAwait(false);
        _gateways.Clear();
        _cts.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Makes a device report now instead of waiting for its interval.</summary>
    public void TriggerUplink(string device) => _devices.First(d => d.Name == device).Wake();

    /// <summary>Moves a device (km).</summary>
    public void MoveDevice(string device, double x, double y)
    {
        var d = _devices.First(d => d.Name == device);
        (d.X, d.Y) = (x, y);
    }

    /// <summary>Describes an application payload of the simulated sensors (Cayenne LPP, water meter, interval command).</summary>
    public static string DescribePayload(byte? fport, ReadOnlySpan<byte> payload)
    {
        if (fport is null || payload.IsEmpty) return "";
        if (fport == 2 && payload.Length == 5)
            return string.Format(CultureInfo.InvariantCulture, "{0:N0} L{1}", BinaryPrimitives.ReadUInt32BigEndian(payload), (payload[4] & 1) != 0 ? ", LEAK" : "");
        if (fport == 10 && payload.Length == 2)
            return string.Format(CultureInfo.InvariantCulture, "set interval {0} s", BinaryPrimitives.ReadUInt16BigEndian(payload));
        if (payload.Length >= 3 && payload.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0) return $"\"{System.Text.Encoding.ASCII.GetString(payload)}\"";
        return CayenneLpp.TryDecode(payload, out var values) ? string.Join(", ", values) : Convert.ToHexString(payload);
    }

    private double NextGaussian()
    {
        lock (_randomGate)
        {
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }

    private double NextDouble()
    {
        lock (_randomGate) return _random.NextDouble();
    }

    /// <summary>Mean RSSI at <paramref name="distanceKm"/>: 14 dBm + 2 dBi minus urban log-distance path loss.</summary>
    public static double MeanRssi(double distanceKm) => 16 - (120 + (35 * Math.Log10(Math.Max(distanceKm, 0.05))));

    private static double SnrOf(double rssi) => Math.Min(rssi + 117, 9.5);

    private int AutoDataRate(LoRaWanSimulatedDevice d)
    {
        var region = _options.Region;
        var best = _options.Gateways.Max(g => SnrOf(MeanRssi(Distance(d, g))));
        for (var dr = region.UplinkMaxDataRate; dr > 0; dr--)
            if (best >= LoRaWanNetworkServer.DemodulationFloor(region.DataRates[dr].SpreadingFactor) + 6) return dr;
        return 0;
    }

    private static double Distance(LoRaWanSimulatedDevice d, LoRaWanSimulatedGateway g) => Math.Sqrt(Math.Pow(d.X - g.X, 2) + Math.Pow(d.Y - g.Y, 2));

    private async Task DeviceLoopAsync(LoRaWanSimulatedDevice d, int index, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300 + (index * 900)), ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            d.Mac.DataRate = d.Options.DataRate ?? AutoDataRate(d);
            if (!d.Mac.IsActivated)
            {
                d.JoinWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                byte[] join;
                lock (d) join = d.Mac.CreateJoinRequest();
                await TransmitUplinkAsync(d, join, ct).ConfigureAwait(false);
                var joined = await Task.WhenAny(d.JoinWaiter.Task, Task.Delay(_options.JoinTimeout, ct)).ConfigureAwait(false) == d.JoinWaiter.Task;
                if (!joined) await Task.Delay(TimeSpan.FromSeconds(2 + (NextDouble() * 4)), ct).ConfigureAwait(false);
                continue;
            }

            var (port, payload) = Measure(d);
            if (d.Uplinks > 0 && d.Uplinks % 10 == 0) lock (d) d.Mac.RequestLinkCheck();
            var confirmed = NextDouble() < d.Options.ConfirmedRatio;
            byte[] uplink;
            lock (d) uplink = d.Mac.CreateUplink(port, payload, confirmed);
            await TransmitUplinkAsync(d, uplink, ct).ConfigureAwait(false);
            var jitter = d.Interval * (0.9 + (NextDouble() * 0.2));
            await d.SleepAsync(jitter, ct).ConfigureAwait(false);
        }
    }

    private (byte Port, byte[] Payload) Measure(LoRaWanSimulatedDevice d)
    {
        var hour = DateTime.Now.TimeOfDay.TotalHours;
        var daily = Math.Sin((hour - 9) / 24 * 2 * Math.PI);
        d.Volts = Math.Max(3.0, d.Volts - 0.0005);
        d.Mac.Battery = (byte)Math.Clamp((d.Volts - 3.0) / 0.7 * 254, 1, 254);
        (byte, byte[]) result;
        switch (d.Options.Sensor)
        {
            case LoRaWanSensorKind.Environment:
                d.Temperature += ((28 + (4 * daily) - d.Temperature) * 0.2) + (NextGaussian() * 0.15);
                d.Humidity = Math.Clamp(d.Humidity + ((75 - (12 * daily) - d.Humidity) * 0.2) + (NextGaussian() * 0.8), 20, 100);
                result = (1, new CayenneLpp().AddTemperature(1, d.Temperature).AddHumidity(2, d.Humidity).AddAnalogInput(3, d.Volts).ToArray());
                break;
            case LoRaWanSensorKind.SoilMoisture:
                d.Soil = Math.Clamp(d.Soil - 0.3 + (NextGaussian() * 0.2) + (d.Soil < 25 ? 25 : 0), 5, 95);
                d.Temperature += ((26 + (2 * daily) - d.Temperature) * 0.1) + (NextGaussian() * 0.05);
                result = (1, new CayenneLpp().AddAnalogInput(1, d.Soil).AddTemperature(2, d.Temperature).AddAnalogInput(3, d.Volts).ToArray());
                break;
            case LoRaWanSensorKind.WaterMeter:
                d.Litres += Math.Max(0, 12 + (NextGaussian() * 6));
                var leak = NextDouble() < 0.05;
                var buffer = new byte[5];
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)d.Litres);
                buffer[4] = leak ? (byte)1 : (byte)0;
                result = (2, buffer);
                break;
            default:
                d.Heading += NextGaussian() * 0.4;
                d.X = Math.Clamp(d.X + (Math.Cos(d.Heading) * 0.12), -2, 7);
                d.Y = Math.Clamp(d.Y + (Math.Sin(d.Heading) * 0.12), -3, 4);
                result = (1, new CayenneLpp().AddGps(1, BaseLatitude + (d.Y / 111.0), BaseLongitude + (d.X / 110.0), 12).AddAnalogInput(2, d.Volts).ToArray());
                break;
        }

        d.LastReading = DescribePayload(result.Item1, result.Item2);
        return result;
    }

    private async Task TransmitUplinkAsync(LoRaWanSimulatedDevice d, byte[] phy, CancellationToken ct)
    {
        var region = _options.Region;
        var rate = region.DataRates[Math.Clamp(d.Mac.DataRate, 0, region.UplinkMaxDataRate)];
        var channel = (int)(NextDouble() * region.UplinkChannels.Count);
        var freq = region.UplinkChannels[channel];
        var receptions = new List<(string, double, double)>();
        var floor = LoRaWanNetworkServer.DemodulationFloor(rate.SpreadingFactor);
        var forwards = new List<Task>();
        foreach (var (g, forwarder) in _gateways)
        {
            var rssi = MeanRssi(Distance(d, g)) + (NextGaussian() * _options.ShadowingDb);
            var snr = Math.Round(SnrOf(rssi) + (NextGaussian() * 0.5), 1);
            if (snr < floor) continue;
            receptions.Add((g.Name, Math.Round(rssi), snr));
            forwards.Add(forwarder.ForwardAsync(new SemtechRxPacket
            {
                Data = phy,
                Frequency = freq,
                Channel = channel,
                DataRate = rate.Datr,
                Rssi = Math.Round(rssi),
                Snr = snr,
            }, ct).AsTask());
        }

        d.Uplinks++;
        if (receptions.Count == 0) d.Lost++;
        await Task.WhenAll(forwards).ConfigureAwait(false);
        var packet = LoRaWanPacket.Decode(phy);
        RadioActivity?.Invoke(this, new LoRaWanRadioEvent
        {
            Time = DateTimeOffset.Now,
            Device = d.Name,
            Uplink = true,
            Phy = phy,
            Frequency = freq,
            DataRate = rate.Datr,
            Airtime = LoRaAirtime.Compute(phy.Length, rate.SpreadingFactor, rate.BandwidthKHz),
            Receptions = receptions,
            Summary = packet.MType == LoRaWanMType.JoinRequest ? "Join-Request" : $"{(packet.IsConfirmed ? "Confirmed" : "Unconfirmed")} FCnt {packet.FCnt}: {d.LastReading}",
        });
    }

    private async ValueTask TransmitDownlinkAsync(LoRaWanSimulatedGateway gateway, SemtechPacketForwarder forwarder, SemtechTxPacket tx)
    {
        if (_options.HonorTimestamps && !tx.Immediate)
        {
            var wait = unchecked((int)(tx.Tmst - forwarder.Timestamp));
            if (wait < -20_000) throw new InvalidOperationException("TOO_LATE");
            if (wait > 30_000_000) throw new InvalidOperationException("TOO_EARLY");
            if (wait > 0) await Task.Delay(TimeSpan.FromTicks(wait * 10L), _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }

        LoRaWanSimulatedDevice? target = null;
        LoRaWanDownlink? received = null;
        foreach (var d in _devices)
        {
            lock (d)
            {
                received = d.Mac.HandleDownlink(tx.Data);
            }

            if (received is null) continue;
            target = d;
            break;
        }

        var summary = "not for any simulated device";
        if (target is not null && received is not null)
        {
            target.Downlinks++;
            if (received.JoinAccepted)
            {
                target.Joins++;
                target.JoinWaiter?.TrySetResult();
                summary = $"Join-Accept, DevAddr {target.Mac.DevAddr}";
            }
            else
            {
                var parts = new List<string>();
                if (received.Ack) parts.Add("ACK");
                if (received.MacCommands.Count > 0) parts.Add(string.Join(", ", received.MacCommands));
                if (received.FPort == 10 && received.Payload.Length == 2)
                {
                    target.Interval = TimeSpan.FromSeconds(Math.Clamp((int)BinaryPrimitives.ReadUInt16BigEndian(received.Payload), 5, 3600));
                    target.Wake();
                }

                if (received.FPort is > 0) parts.Add($"FPort {received.FPort}: {DescribePayload(received.FPort, received.Payload)}");
                if (received.FPending) parts.Add("more pending");
                summary = parts.Count == 0 ? "empty downlink" : string.Join("; ", parts);
                if (received.Confirmed || target.Mac.PendingMacCommands > 0 || received.FPending) target.Wake();
            }
        }

        RadioActivity?.Invoke(this, new LoRaWanRadioEvent
        {
            Time = DateTimeOffset.Now,
            Device = target?.Name,
            Uplink = false,
            Phy = tx.Data,
            Frequency = tx.Frequency,
            DataRate = tx.DataRate,
            Airtime = LoRaAirtime.Compute(tx.Data.Length, tx.DataRate, crc: false),
            Gateway = gateway.Name,
            Summary = summary,
        });
    }
}
