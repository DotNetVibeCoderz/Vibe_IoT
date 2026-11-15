namespace IoTCom.Net.Protocols.Bacnet;

/// <summary>
/// An air handling unit controller as a <see cref="BacnetDevice"/>: supply fan with start/stop command and proof,
/// chilled-water valve and outdoor-air damper driven by their own loops unless commanded, supply/return/outdoor air
/// temperatures, CO₂, duct static pressure, set points, an occupancy mode and a filter alarm.
/// </summary>
/// <remarks>
/// Objects: AI1 supply air temperature, AI2 return air temperature, AI3 outdoor air temperature, AI4 return CO₂,
/// AI5 duct static pressure; AO1 cooling valve, AO2 outdoor air damper; AV1 supply temperature set point, AV2 CO₂ set
/// point; BO1 supply fan command; BI1 supply fan status, BI2 filter alarm; MSV1 occupancy mode
/// (Occupied, Unoccupied, Standby).
/// </remarks>
public sealed class BacnetAhuSimulator : IAsyncDisposable
{
    private double _fanSpeed;     // 0..1, lags the command
    private double _occupants = 18;
    private readonly Random _random;

    private BacnetAhuSimulator(BacnetDevice device, int seed)
    {
        Device = device;
        _random = new Random(seed);
        SupplyTemp = device.Add(BacnetObject.AnalogInput(1, "AHU-1 Supply Air Temp", BacnetUnits.DegreesCelsius, 24.0, 0.2f));
        ReturnTemp = device.Add(BacnetObject.AnalogInput(2, "AHU-1 Return Air Temp", BacnetUnits.DegreesCelsius, 25.6, 0.2f));
        OutdoorTemp = device.Add(BacnetObject.AnalogInput(3, "Outdoor Air Temp", BacnetUnits.DegreesCelsius, 32.4, 0.5f));
        Co2 = device.Add(BacnetObject.AnalogInput(4, "AHU-1 Return CO2", BacnetUnits.PartsPerMillion, 640, 25f));
        StaticPressure = device.Add(BacnetObject.AnalogInput(5, "AHU-1 Duct Static Pressure", BacnetUnits.Pascals, 0, 10f));
        CoolingValve = device.Add(BacnetObject.AnalogOutput(1, "AHU-1 CHW Valve", BacnetUnits.Percent, 0, 2f));
        Damper = device.Add(BacnetObject.AnalogOutput(2, "AHU-1 OA Damper", BacnetUnits.Percent, 20, 2f));
        SupplySetpoint = device.Add(BacnetObject.AnalogValue(1, "AHU-1 Supply Temp Setpoint", BacnetUnits.DegreesCelsius, 14.0));
        Co2Setpoint = device.Add(BacnetObject.AnalogValue(2, "AHU-1 CO2 Setpoint", BacnetUnits.PartsPerMillion, 800, covIncrement: 10f));
        FanCommand = device.Add(BacnetObject.BinaryOutput(1, "AHU-1 Supply Fan Command", true, "Start", "Stop"));
        FanStatus = device.Add(BacnetObject.BinaryInput(1, "AHU-1 Supply Fan Status", false, "Running", "Stopped"));
        FilterAlarm = device.Add(BacnetObject.BinaryInput(2, "AHU-1 Filter Alarm", false, "Dirty", "Clean"));
        Occupancy = device.Add(BacnetObject.MultiStateValue(1, "AHU-1 Occupancy Mode", ["Occupied", "Unoccupied", "Standby"], 1));
        foreach (var o in device.Objects) o.Description = o.Name.Replace("AHU-1 ", "", StringComparison.Ordinal);
    }

    /// <summary>Creates the controller; configure the device (instance, transport, broadcast address) as usual.</summary>
    public static BacnetAhuSimulator Create(Action<BacnetDeviceOptions>? configure = null, int seed = 3)
    {
        var device = BacnetDevice.Create(o =>
        {
            o.DeviceInstance = 2604;
            o.DeviceName = "AHU-1 Lantai 3";
            o.ModelName = "IoTCom AHU Controller";
            o.Description = "Air handling unit, 3rd floor, Gedung Sate Annex";
            configure?.Invoke(o);
        });
        return new BacnetAhuSimulator(device, seed);
    }

    /// <summary>The BACnet device.</summary>
    public BacnetDevice Device { get; }

    /// <summary>AI1.</summary>
    public BacnetObject SupplyTemp { get; }

    /// <summary>AI2.</summary>
    public BacnetObject ReturnTemp { get; }

    /// <summary>AI3.</summary>
    public BacnetObject OutdoorTemp { get; }

    /// <summary>AI4.</summary>
    public BacnetObject Co2 { get; }

    /// <summary>AI5.</summary>
    public BacnetObject StaticPressure { get; }

    /// <summary>AO1.</summary>
    public BacnetObject CoolingValve { get; }

    /// <summary>AO2.</summary>
    public BacnetObject Damper { get; }

    /// <summary>AV1.</summary>
    public BacnetObject SupplySetpoint { get; }

    /// <summary>AV2.</summary>
    public BacnetObject Co2Setpoint { get; }

    /// <summary>BO1.</summary>
    public BacnetObject FanCommand { get; }

    /// <summary>BI1.</summary>
    public BacnetObject FanStatus { get; }

    /// <summary>BI2.</summary>
    public BacnetObject FilterAlarm { get; }

    /// <summary>MSV1.</summary>
    public BacnetObject Occupancy { get; }

    /// <summary>Starts the device.</summary>
    public ValueTask StartAsync(CancellationToken ct = default) => Device.StartAsync(ct);

    /// <summary>Advances the process: fan ramp, supply temperature control, CO₂ and damper, pressure.</summary>
    public void Step(double seconds)
    {
        var occupied = Occupancy.Value == 1;
        var command = FanCommand.Value == 1 && Occupancy.Value != 2;
        _fanSpeed = Math.Clamp(_fanSpeed + ((command ? 1 : -1) * seconds / 20), 0, 1);   // 20 s ramp
        FanStatus.Update(_fanSpeed > 0.5 ? 1 : 0);
        StaticPressure.Update(Math.Round((250 * _fanSpeed * _fanSpeed) + (_random.NextDouble() * 4) - 2, 1));

        // The loops only drive outputs nobody overrides (relinquish default = automatic).
        var supply = SupplyTemp.Value;
        if (CoolingValve.ActivePriority is null)
        {
            var error = supply - SupplySetpoint.Value;
            CoolingValve.Update(Math.Clamp(CoolingValve.RelinquishDefault.AsDouble()!.Value + (error * 4 * seconds / 10), 0, 100));
        }

        if (Damper.ActivePriority is null)
        {
            var co2Error = Co2.Value - Co2Setpoint.Value;
            Damper.Update(Math.Clamp(Damper.RelinquishDefault.AsDouble()!.Value + (co2Error * 0.05 * seconds / 10), 15, 100));
        }

        var outdoor = OutdoorTemp.Value;
        var mixed = (Damper.Value / 100 * outdoor) + ((1 - (Damper.Value / 100)) * ReturnTemp.Value);
        var target = _fanSpeed < 0.1 ? ReturnTemp.Value : mixed - (CoolingValve.Value / 100 * 16 * _fanSpeed);
        SupplyTemp.Update(Math.Round(supply + ((target - supply) * Math.Min(1, seconds / 30)), 2));

        if (occupied && _random.NextDouble() < 0.05 * seconds) _occupants = Math.Clamp(_occupants + _random.Next(-4, 5), 0, 60);
        if (!occupied) _occupants = Math.Max(0, _occupants - seconds / 10);
        var ventilation = _fanSpeed * Damper.Value / 100;
        var co2 = Co2.Value + ((_occupants * 1.2) - ((Co2.Value - 420) * ventilation * 0.08)) * seconds / 60;
        Co2.Update(Math.Round(Math.Max(420, co2)));
        var room = ReturnTemp.Value + (((_occupants * 0.004) + ((outdoor - ReturnTemp.Value) * 0.002) - ((ReturnTemp.Value - SupplyTemp.Value) * 0.01 * _fanSpeed)) * seconds / 6);
        ReturnTemp.Update(Math.Round(room, 2));
        OutdoorTemp.Update(Math.Round(outdoor + ((_random.NextDouble() - 0.5) * 0.02 * seconds), 2));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Device.DisposeAsync();
}
