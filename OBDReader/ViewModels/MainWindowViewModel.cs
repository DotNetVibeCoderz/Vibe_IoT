using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using OBDReader.Services;
using ReactiveUI;

namespace OBDReader.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IObdService _obdService;
    private bool _isConnected;
    private string _selectedPort = "";
    private double _rpm;
    private int _speed;
    private int _temp;
    private double _voltage;
    private string _statusMessage = "";
    private string _analysisText = "";
    private bool _isBusy;
    private bool _useSimulator;
    
    // UI Properties
    public ObservableCollection<string> PortList { get; } = new();

    public string SelectedPort
    {
        get => _selectedPort;
        set => this.RaiseAndSetIfChanged(ref _selectedPort, value);
    }

    public bool UseSimulator
    {
        get => _useSimulator;
        set 
        {
            this.RaiseAndSetIfChanged(ref _useSimulator, value);
            // Notify that UseDevice property changed as well if we were binding inversely, 
            // but we can just use !UseSimulator in XAML.
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public double Rpm
    {
        get => _rpm;
        set => this.RaiseAndSetIfChanged(ref _rpm, value);
    }

    public int Speed
    {
        get => _speed;
        set => this.RaiseAndSetIfChanged(ref _speed, value);
    }

    public int Temp
    {
        get => _temp;
        set => this.RaiseAndSetIfChanged(ref _temp, value);
    }
    
    public double Voltage
    {
        get => _voltage;
        set => this.RaiseAndSetIfChanged(ref _voltage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }
    
    public string AnalysisText
    {
        get => _analysisText;
        set => this.RaiseAndSetIfChanged(ref _analysisText, value);
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPortsCommand { get; }

    public MainWindowViewModel()
    {
        _obdService = new Elm327Service();
        
        // Default to Simulator for easier testing
        UseSimulator = true;
        
        RefreshPorts();

        ConnectCommand = ReactiveCommand.CreateFromTask(ToggleConnectionAsync);
        RefreshPortsCommand = ReactiveCommand.Create(RefreshPorts);
        
        StatusMessage = "Ready to connect.";
        AnalysisText = "No data analyzed yet.";
        
        DispatcherTimer.Run(PollData, TimeSpan.FromMilliseconds(200));
    }

    private void RefreshPorts()
    {
        PortList.Clear();
        var ports = _obdService.GetAvailablePorts();
        foreach (var port in ports)
        {
            PortList.Add(port);
        }
        if (PortList.Count > 0) SelectedPort = PortList[0];
    }

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            _obdService.Disconnect();
            IsConnected = false;
            StatusMessage = "Disconnected.";
        }
        else
        {
            string portToUse;
            if (UseSimulator)
            {
                portToUse = "SIMULATION_MODE";
                StatusMessage = "Starting Simulator...";
            }
            else
            {
                if (string.IsNullOrEmpty(SelectedPort))
                {
                    StatusMessage = "Please select a COM port.";
                    return;
                }
                portToUse = SelectedPort;
                StatusMessage = $"Connecting to {portToUse}...";
            }

            IsBusy = true;
            bool success = await _obdService.ConnectAsync(portToUse, 38400);
            IsBusy = false;

            if (success)
            {
                IsConnected = true;
                StatusMessage = UseSimulator ? "Simulator Running" : "Connected to Device";
            }
            else
            {
                StatusMessage = "Connection Failed.";
            }
        }
    }

    private bool PollData()
    {
        if (IsConnected)
        {
            _ = FetchData();
        }
        return true;
    }

    private async Task FetchData()
    {
        if (_isBusy && !_obdService.IsConnected) return;

        var data = await _obdService.ReadDataAsync();
        
        Rpm = data.Rpm;
        Speed = data.Speed;
        Temp = data.CoolantTemp;
        Voltage = data.BatteryVoltage;
        
        AnalyzeData(data);
    }

    private void AnalyzeData(Models.ObdData data)
    {
        string analysis = "Normal Operation";
        
        if (data.Rpm > 4500) analysis = "WARNING: High RPM!";
        if (data.CoolantTemp > 100) analysis = "DANGER: Overheating!";
        if (data.Speed > 120) analysis = "CAUTION: Overspeeding";
        if (data.BatteryVoltage < 12.0) analysis = "Battery Low";
        
        AnalysisText = analysis;
    }
}