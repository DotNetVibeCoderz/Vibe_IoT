using System.Buffers.Binary;

namespace IoTCom.Net.Protocols.EtherCat;

/// <summary>
/// A simulated EtherCAT I/O rack on a conveyor station: a bus coupler, an 8-channel digital input terminal, an
/// 8-channel digital output terminal and a 4-channel analog input terminal. Outputs: 0 conveyor motor, 1 horn,
/// 2 green lamp, 3 red lamp. Inputs: 0 motor running feedback, 1 part sensor, 2 emergency stop OK, 3 guard door closed.
/// Analog (0.01 units): motor current A, gearbox temperature °C, air pressure bar, belt speed m/min.
/// </summary>
public sealed class EthercatRackSimulator
{
    /// <summary>Vendor ID used by the simulated devices (not an ETG-assigned ID).</summary>
    public const uint SimVendor = 0x00000E5C;

    /// <summary>Creates the rack on a new segment.</summary>
    public EthercatRackSimulator(int seed = 7)
    {
        Segment = new VirtualEthercatSegment(seed);
        Coupler = Segment.Add(new VirtualEcatSlave("Sim EK coupler", SimVendor, 0x0001, 0, 0, mailbox: false, serial: 1001));
        DigitalIn = Segment.Add(new DigitalInputs(this));
        DigitalOut = Segment.Add(new VirtualEcatSlave("Sim DO8 24V", SimVendor, 0x0003, 1, 0, serial: 1003));
        Analog = Segment.Add(new AnalogInputs(this));
    }

    /// <summary>The segment to give the master (<c>UseLink(rack.Segment)</c>).</summary>
    public VirtualEthercatSegment Segment { get; }

    /// <summary>Bus coupler (no process data).</summary>
    public VirtualEcatSlave Coupler { get; }

    /// <summary>8 digital inputs.</summary>
    public VirtualEcatSlave DigitalIn { get; }

    /// <summary>8 digital outputs.</summary>
    public VirtualEcatSlave DigitalOut { get; }

    /// <summary>4 analog inputs (int16, 0.01 units), with a writable filter setting at 0x8000:15.</summary>
    public VirtualEcatSlave Analog { get; }

    /// <summary>True when the motor output is on (and the terminal is in OP).</summary>
    public bool MotorCommanded => (DigitalOut.Outputs[0] & 0x01) != 0;

    /// <summary>Motor running feedback.</summary>
    public bool MotorRunning { get; private set; }

    /// <summary>Emergency stop released.</summary>
    public bool EmergencyStopOk { get; private set; } = true;

    /// <summary>Guard door closed.</summary>
    public bool GuardClosed { get; set; } = true;

    /// <summary>Parts counted by the sensor.</summary>
    public int Parts { get; private set; }

    /// <summary>Belt speed, m/min.</summary>
    public double BeltSpeed { get; private set; }

    /// <summary>Motor current, A.</summary>
    public double MotorCurrent { get; private set; }

    /// <summary>Gearbox temperature, °C.</summary>
    public double GearboxTemperature { get; private set; } = 24;

    /// <summary>Air pressure, bar.</summary>
    public double AirPressure { get; private set; } = 6.1;

    private double _partPhase, _clock;
    private bool _partSensor;

    /// <summary>Presses (true) or releases (false) the emergency stop.</summary>
    public void PressEmergencyStop(bool pressed = true) => EmergencyStopOk = !pressed;

    /// <summary>Advances the station.</summary>
    public void Step(double seconds) => Segment.Step(seconds);

    private void Update(double dt)
    {
        _clock += dt;
        var run = MotorCommanded && EmergencyStopOk && GuardClosed;
        var target = run ? 18.0 : 0.0;
        BeltSpeed += (target - BeltSpeed) * Math.Min(1, dt * 2.5);
        MotorRunning = BeltSpeed > 2;
        MotorCurrent = (run ? 3.1 + (0.15 * Math.Sin(_clock * 3)) : 0) + (BeltSpeed * 0.02);
        GearboxTemperature += ((run ? 52 : 24) - GearboxTemperature) * Math.Min(1, dt / 240);
        AirPressure = 6.1 + (0.05 * Math.Sin(_clock / 7));
        _partPhase += BeltSpeed / 60 * dt / 0.6;   // a part every 0.6 m
        var sensor = _partPhase % 1 is > 0.8 and < 0.95;
        if (sensor && !_partSensor) Parts++;
        _partSensor = sensor;
    }

    private sealed class DigitalInputs(EthercatRackSimulator rack) : VirtualEcatSlave("Sim DI8 24V", SimVendor, 0x0002, 0, 1, serial: 1002)
    {
        public override void Step(double seconds)
        {
            rack.Update(seconds);
            InputMemory[0] = (byte)((rack.MotorRunning ? 1 : 0) | (rack._partSensor ? 2 : 0) | (rack.EmergencyStopOk ? 4 : 0) | (rack.GuardClosed ? 8 : 0));
        }
    }

    private sealed class AnalogInputs : VirtualEcatSlave
    {
        private readonly EthercatRackSimulator _rack;

        public AnalogInputs(EthercatRackSimulator rack) : base("Sim AI4 10V", SimVendor, 0x0004, 0, 8, serial: 1004)
        {
            _rack = rack;
            AddObject(0x8000, 0x15, [0, 0], writable: true);   // filter: 0 = 50 Hz, 1 = 60 Hz, 2 = 100 Hz, 3 = 500 Hz
            AddObject(0x6000, 0x11, [0, 0]);
        }

        public override void Step(double seconds)
        {
            Span<short> values = [(short)(_rack.MotorCurrent * 100), (short)(_rack.GearboxTemperature * 100), (short)(_rack.AirPressure * 100), (short)(_rack.BeltSpeed * 100)];
            for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt16LittleEndian(InputMemory[(i * 2)..], values[i]);
            AddObject(0x6000, 0x11, BitConverter.GetBytes(values[0]));
        }

        protected override uint OnObjectWrite(ushort index, byte subIndex, byte[] value)
            => index == 0x8000 && subIndex == 0x15 && BinaryPrimitives.ReadUInt16LittleEndian(value) > 3 ? 0x06090030u : 0;
    }
}
