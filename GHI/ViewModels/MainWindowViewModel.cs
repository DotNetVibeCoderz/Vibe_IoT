using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GHI.Drivers;
using ReactiveUI;

namespace GHI.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly FezHatDriver _driver;
        private Timer _timer;

        // Simulation Status
        private bool _isSimulation;
        public bool IsSimulation 
        { 
            get => _isSimulation; 
            set 
            {
                // Only update if value changed to prevent loops if we set it back
                if (_isSimulation != value)
                {
                    _driver.SetSimulationMode(value);
                    
                    // Read back the actual state (it might have failed to go to Device mode)
                    var actualState = _driver.IsSimulation;
                    
                    RaiseAndSetIfChanged(ref _isSimulation, actualState);
                    
                    // Notify that IsDevice changed too
                    OnPropertyChanged(nameof(IsDevice));
                }
            } 
        }

        public bool IsDevice
        {
            get => !IsSimulation;
            set => IsSimulation = !value;
        }

        // Sensor Properties
        private double _lightLevel;
        public double LightLevel { get => _lightLevel; set => RaiseAndSetIfChanged(ref _lightLevel, value); }

        private double _temperature;
        public double Temperature { get => _temperature; set => RaiseAndSetIfChanged(ref _temperature, value); }

        private double _accelX;
        public double AccelX { get => _accelX; set => RaiseAndSetIfChanged(ref _accelX, value); }
        private double _accelY;
        public double AccelY { get => _accelY; set => RaiseAndSetIfChanged(ref _accelY, value); }
        private double _accelZ;
        public double AccelZ { get => _accelZ; set => RaiseAndSetIfChanged(ref _accelZ, value); }

        private bool _isButton18Pressed;
        public bool IsButton18Pressed { get => _isButton18Pressed; set => RaiseAndSetIfChanged(ref _isButton18Pressed, value); }

        private bool _isButton22Pressed;
        public bool IsButton22Pressed { get => _isButton22Pressed; set => RaiseAndSetIfChanged(ref _isButton22Pressed, value); }

        // Actuator Properties (Bound to UI controls)
        
        // DIO 24 LED
        private bool _led24State;
        public bool Led24State 
        { 
            get => _led24State; 
            set 
            { 
                if (RaiseAndSetIfChanged(ref _led24State, value)) 
                    _driver.SetDio24(value);
            } 
        }

        // Motors
        private double _motorASpeed;
        public double MotorASpeed
        {
            get => _motorASpeed;
            set
            {
                if (RaiseAndSetIfChanged(ref _motorASpeed, value))
                    _driver.SetMotorA(value);
            }
        }

        private double _motorBSpeed;
        public double MotorBSpeed
        {
            get => _motorBSpeed;
            set
            {
                if (RaiseAndSetIfChanged(ref _motorBSpeed, value))
                    _driver.SetMotorB(value);
            }
        }

        // Servos
        private double _servo1Pos;
        public double Servo1Pos
        {
            get => _servo1Pos;
            set
            {
                if (RaiseAndSetIfChanged(ref _servo1Pos, value))
                    _driver.SetServo1(value);
            }
        }

        private double _servo2Pos;
        public double Servo2Pos
        {
            get => _servo2Pos;
            set
            {
                if (RaiseAndSetIfChanged(ref _servo2Pos, value))
                    _driver.SetServo2(value);
            }
        }

        // RGB LED D2
        private double _d2R; public double D2R { get => _d2R; set { if(RaiseAndSetIfChanged(ref _d2R, value)) UpdateD2(); } }
        private double _d2G; public double D2G { get => _d2G; set { if(RaiseAndSetIfChanged(ref _d2G, value)) UpdateD2(); } }
        private double _d2B; public double D2B { get => _d2B; set { if(RaiseAndSetIfChanged(ref _d2B, value)) UpdateD2(); } }

        // RGB LED D3
        private double _d3R; public double D3R { get => _d3R; set { if(RaiseAndSetIfChanged(ref _d3R, value)) UpdateD3(); } }
        private double _d3G; public double D3G { get => _d3G; set { if(RaiseAndSetIfChanged(ref _d3G, value)) UpdateD3(); } }
        private double _d3B; public double D3B { get => _d3B; set { if(RaiseAndSetIfChanged(ref _d3B, value)) UpdateD3(); } }


        public MainWindowViewModel()
        {
            // Initialize Driver with Simulation Default = true
            _driver = new FezHatDriver(useSimulation: true);
            
            // Reflect status
            _isSimulation = _driver.IsSimulation;

            // Start Polling Timer (100ms)
            _timer = new Timer(PollSensors, null, 100, 100);
        }

        private void PollSensors(object? state)
        {
            try
            {
                // In simulation, these will return fake data
                var light = _driver.GetLightLevel();
                var temp = _driver.GetTemperature();
                var accel = _driver.GetAcceleration();
                var b18 = _driver.IsDio18Pressed();
                var b22 = _driver.IsDio22Pressed();

                // Marshal to UI Thread
                Dispatcher.UIThread.Post(() =>
                {
                    LightLevel = light;
                    Temperature = temp;
                    AccelX = accel.X;
                    AccelY = accel.Y;
                    AccelZ = accel.Z;
                    IsButton18Pressed = b18;
                    IsButton22Pressed = b22;
                });
            }
            catch
            {
                // Ignore sensor errors in loop to keep UI alive
            }
        }

        private void UpdateD2() => _driver.SetLedD2(D2R, D2G, D2B);
        private void UpdateD3() => _driver.SetLedD3(D3R, D3G, D3B);

        public void Dispose()
        {
            _timer?.Dispose();
            _driver?.Dispose();
        }
    }
}