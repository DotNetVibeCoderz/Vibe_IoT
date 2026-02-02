using System;
using System.Device.Gpio;
using System.Device.I2c;
using System.Threading;
using Iot.Device.Pwm;

namespace GHI.Drivers
{
    public class FezHatDriver : IDisposable
    {
        // I2C Addresses
        private const int I2cBusId = 1;
        private const int Pca9685Address = 0x40; 
        private const int Ads7830Address = 0x48; 
        private const int Mma8453Address = 0x1C; 

        // Components
        private GpioController? _gpio;
        private I2cDevice? _i2cPwm;
        private I2cDevice? _i2cAdc;
        private I2cDevice? _i2cAccel;

        private Pca9685? _pwmDriver;

        // GPIO Pins
        private const int PinMotorEnable = 12;
        private const int PinDio18 = 18;
        private const int PinDio22 = 22;
        private const int PinDio24 = 24;
        
        // Motor Pins
        private const int PinMotorADir1 = 27;
        private const int PinMotorADir2 = 23;
        private const int PinMotorBDir1 = 6;
        private const int PinMotorBDir2 = 5;

        // PWM Channels
        private const int PwmChMotorA = 14;
        private const int PwmChMotorB = 13;
        private const int PwmChLedD2R = 1;
        private const int PwmChLedD2G = 0;
        private const int PwmChLedD2B = 2;
        private const int PwmChLedD3R = 4;
        private const int PwmChLedD3G = 3;
        private const int PwmChLedD3B = 15;
        private const int PwmChServo1 = 9;
        private const int PwmChServo2 = 10;

        private bool _disposed = false;

        // Simulation
        public bool IsSimulation { get; private set; }
        private double _simTime = 0;
        private Random _simRandom = new Random();

        public FezHatDriver(bool useSimulation = true)
        {
            IsSimulation = useSimulation;
            Initialize();
        }

        public void SetSimulationMode(bool useSimulation)
        {
            DisposeHardware();
            IsSimulation = useSimulation;
            Initialize();
        }

        private void Initialize()
        {
            if (IsSimulation)
            {
                Console.WriteLine("FezHatDriver initialized in SIMULATION mode.");
                return;
            }

            Console.WriteLine("Attempting to initialize Hardware...");

            // 1. GPIO
            try
            {
                 _gpio = new GpioController();
                 _gpio.OpenPin(PinDio18, PinMode.InputPullUp);
                 _gpio.OpenPin(PinDio22, PinMode.InputPullUp);
                 _gpio.OpenPin(PinDio24, PinMode.Output);
                 _gpio.OpenPin(PinMotorEnable, PinMode.Output);
                 _gpio.Write(PinMotorEnable, PinValue.High); 
                 _gpio.OpenPin(PinMotorADir1, PinMode.Output);
                 _gpio.OpenPin(PinMotorADir2, PinMode.Output);
                 _gpio.OpenPin(PinMotorBDir1, PinMode.Output);
                 _gpio.OpenPin(PinMotorBDir2, PinMode.Output);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GPIO Init Failed: {ex.Message}. Switching to Simulation.");
                DisposeHardware();
                IsSimulation = true; // Fallback
                return;
            }

            // 2. I2C Devices
            try
            {
                _i2cPwm = I2cDevice.Create(new I2cConnectionSettings(I2cBusId, Pca9685Address));
                _pwmDriver = new Pca9685(_i2cPwm);
                _pwmDriver.PwmFrequency = 50; 
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"PWM Init Failed: {ex.Message}");
            }

            try
            {
                _i2cAdc = I2cDevice.Create(new I2cConnectionSettings(I2cBusId, Ads7830Address));
            }
            catch (Exception ex) { Console.WriteLine($"ADC Init Failed: {ex.Message}"); }

            try
            {
                _i2cAccel = I2cDevice.Create(new I2cConnectionSettings(I2cBusId, Mma8453Address));
                // Activate Accelerometer (Register 0x2A, Value 0x01)
                _i2cAccel.Write(new byte[] { 0x2A, 0x01 });
            }
            catch (Exception ex) { Console.WriteLine($"Accel Init Failed: {ex.Message}"); }
        }

        // --- SENSORS ---

        public bool IsDio18Pressed()
        {
            if (IsSimulation)
            {
                // Simulate button press every 5 seconds roughly
                return (_simTime % 50) < 5; // Assuming called every 100ms, this is 0.5s press every 5s
            }
            return _gpio?.IsPinOpen(PinDio18) == true ? _gpio.Read(PinDio18) == PinValue.Low : false;
        }

        public bool IsDio22Pressed()
        {
            if (IsSimulation)
            {
                 return (_simTime % 70) < 5;
            }
            return _gpio?.IsPinOpen(PinDio22) == true ? _gpio.Read(PinDio22) == PinValue.Low : false;
        }

        public double GetLightLevel()
        {
            if (IsSimulation)
            {
                _simTime += 0.1; // Increment time
                // Sine wave simulation 0..1
                return (Math.Sin(_simTime * 0.5) + 1.0) / 2.0; 
            }

            if (_i2cAdc == null) return 0;
            try 
            {
                _i2cAdc.WriteByte(0xDC); 
                return _i2cAdc.ReadByte() / 255.0; 
            }
            catch { return 0; }
        }

        public double GetTemperature()
        {
            if (IsSimulation)
            {
                // Random walk around 25 degrees
                return 25.0 + (Math.Cos(_simTime * 0.2) * 5.0) + (_simRandom.NextDouble() - 0.5);
            }

            if (_i2cAdc == null) return 0;
            try
            {
                _i2cAdc.WriteByte(0x94);
                var raw = _i2cAdc.ReadByte();
                var voltageMv = (raw / 255.0) * 3300.0;
                return (voltageMv - 450.0) / 19.5;
            }
            catch { return 0; }
        }

        public (double X, double Y, double Z) GetAcceleration()
        {
            if (IsSimulation)
            {
                // Rotating gravity vector
                double x = Math.Sin(_simTime);
                double y = Math.Cos(_simTime);
                double z = Math.Sin(_simTime * 0.5);
                return (x, y, z);
            }

            if (_i2cAccel == null) return (0, 0, 0);
            try {
                _i2cAccel.WriteByte(0x01);
                byte[] buffer = new byte[6];
                _i2cAccel.Read(buffer);
                
                sbyte x = (sbyte)buffer[0];
                sbyte y = (sbyte)buffer[2];
                sbyte z = (sbyte)buffer[4];
                
                return (x / 64.0, y / 64.0, z / 64.0);
            } catch { return (0,0,0); }
        }

        // --- ACTUATORS ---

        public void SetDio24(bool on)
        {
            if (IsSimulation)
            {
                // Just pretend
                return;
            }

            if (_gpio?.IsPinOpen(PinDio24) == true)
                _gpio.Write(PinDio24, on ? PinValue.High : PinValue.Low);
        }

        public void SetMotorA(double speed)
        {
            if (IsSimulation) return;
            SetMotor(speed, PinMotorADir1, PinMotorADir2, PwmChMotorA);
        }

        public void SetMotorB(double speed)
        {
            if (IsSimulation) return;
            SetMotor(speed, PinMotorBDir1, PinMotorBDir2, PwmChMotorB);
        }

        private void SetMotor(double speed, int pinDir1, int pinDir2, int pwmCh)
        {
            if (_pwmDriver == null || _gpio == null) return;
            if (speed > 1.0) speed = 1.0;
            if (speed < -1.0) speed = -1.0;

            if (speed > 0)
            {
                _gpio.Write(pinDir1, PinValue.High);
                _gpio.Write(pinDir2, PinValue.Low);
            }
            else
            {
                _gpio.Write(pinDir1, PinValue.Low);
                _gpio.Write(pinDir2, PinValue.High);
            }

            _pwmDriver.SetDutyCycle(pwmCh, Math.Abs(speed));
        }

        public void SetLedD2(double r, double g, double b)
        {
            if (IsSimulation) return;
            SetPwm(PwmChLedD2R, r);
            SetPwm(PwmChLedD2G, g);
            SetPwm(PwmChLedD2B, b);
        }

        public void SetLedD3(double r, double g, double b)
        {
            if (IsSimulation) return;
            SetPwm(PwmChLedD3R, r);
            SetPwm(PwmChLedD3G, g);
            SetPwm(PwmChLedD3B, b);
        }

        public void SetServo1(double position)
        {
            if (IsSimulation) return;
            SetServo(PwmChServo1, position);
        }

        public void SetServo2(double position)
        {
            if (IsSimulation) return;
            SetServo(PwmChServo2, position);
        }

        private void SetServo(int channel, double position)
        {
            if (_pwmDriver == null) return;
            double minDuty = 0.05; // 1ms
            double maxDuty = 0.10; // 2ms
            double duty = (position * (maxDuty - minDuty)) + minDuty;
            _pwmDriver.SetDutyCycle(channel, duty);
        }

        private void SetPwm(int channel, double duty)
        {
            if (_pwmDriver == null) return;
            if (duty < 0) duty = 0;
            if (duty > 1) duty = 1;
            _pwmDriver.SetDutyCycle(channel, duty);
        }

        private void DisposeHardware()
        {
            if (!IsSimulation)
            {
                try {
                    SetMotorA(0);
                    SetMotorB(0);
                } catch {}
            }

            _pwmDriver?.Dispose(); 
            _pwmDriver = null;
            
            _i2cPwm?.Dispose(); 
            _i2cPwm = null;
            
            _i2cAdc?.Dispose(); 
            _i2cAdc = null;
            
            _i2cAccel?.Dispose(); 
            _i2cAccel = null;
            
            _gpio?.Dispose(); 
            _gpio = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            DisposeHardware();

            _disposed = true;
        }
    }
}