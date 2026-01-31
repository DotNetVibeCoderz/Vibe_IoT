using System;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OBDReader.Models;

namespace OBDReader.Services;

public class Elm327Service : IObdService
{
    private SerialPort? _serialPort;
    private bool _isConnected;
    private bool _isSimulationMode;
    private readonly Random _random = new Random();
    
    // Mock data state
    private double _mockRpm = 0;
    private int _mockSpeed = 0;
    private int _mockTemp = 80;

    public bool IsConnected => _isConnected;

    public string[] GetAvailablePorts()
    {
        // Return only real ports now, UI will handle the Simulation switch
        return SerialPort.GetPortNames();
    }

    public async Task<bool> ConnectAsync(string portName, int baudRate)
    {
        if (portName == "SIMULATION_MODE")
        {
            _isSimulationMode = true;
            _isConnected = true;
            return true;
        }

        _isSimulationMode = false;
        try
        {
            _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
            _serialPort.ReadTimeout = 2000;
            _serialPort.WriteTimeout = 2000;
            _serialPort.Open();
            
            // Initialize ELM327
            await SendCommandAsync("ATZ"); // Reset
            await Task.Delay(1000);
            await SendCommandAsync("ATE0"); // Echo Off
            await SendCommandAsync("ATSP0"); // Auto Protocol

            _isConnected = true;
            return true;
        }
        catch (Exception)
        {
            _isConnected = false;
            return false;
        }
    }

    public void Disconnect()
    {
        if (_serialPort != null && _serialPort.IsOpen)
        {
            _serialPort.Close();
        }
        _isConnected = false;
        _isSimulationMode = false;
    }

    public async Task<ObdData> ReadDataAsync()
    {
        var data = new ObdData();

        if (!_isConnected) return data;

        if (_isSimulationMode)
        {
            return GenerateMockData();
        }

        try
        {
            // Read RPM (01 0C)
            string rpmResponse = await SendCommandAsync("010C");
            data.Rpm = ParseRpm(rpmResponse);

            // Read Speed (01 0D)
            string speedResponse = await SendCommandAsync("010D");
            data.Speed = ParseSpeed(speedResponse);

            // Read Coolant (01 05)
            string tempResponse = await SendCommandAsync("0105");
            data.CoolantTemp = ParseTemp(tempResponse);

            data.ConnectionStatus = "Connected (Live)";
        }
        catch (Exception ex)
        {
            data.LastMessage = "Error reading data: " + ex.Message;
            data.ConnectionStatus = "Error";
            _isConnected = false; // Disconnect on error
        }

        return data;
    }

    private async Task<string> SendCommandAsync(string cmd)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return "";

        _serialPort.WriteLine(cmd + "\r");
        
        // Simple read loop
        return await Task.Run(() => 
        {
            try
            {
                // Wait a bit for response
                Thread.Sleep(50);
                string response = _serialPort.ReadExisting();
                return response.Replace(">", "").Trim();
            }
            catch
            {
                return "";
            }
        });
    }

    private double ParseRpm(string response)
    {
        try
        {
            if (response.Contains("NO DATA") || string.IsNullOrEmpty(response)) return 0;
            string hex = CleanResponse(response);
            if (hex.Length >= 4)
            {
                int a = Convert.ToInt32(hex.Substring(0, 2), 16);
                int b = Convert.ToInt32(hex.Substring(2, 2), 16);
                return ((a * 256.0) + b) / 4.0;
            }
        }
        catch { }
        return 0;
    }

    private int ParseSpeed(string response)
    {
        try
        {
            if (response.Contains("NO DATA") || string.IsNullOrEmpty(response)) return 0;
            string hex = CleanResponse(response);
            if (hex.Length >= 2)
            {
                return Convert.ToInt32(hex.Substring(0, 2), 16);
            }
        }
        catch { }
        return 0;
    }

    private int ParseTemp(string response)
    {
        try
        {
            if (response.Contains("NO DATA") || string.IsNullOrEmpty(response)) return 0;
            string hex = CleanResponse(response);
            if (hex.Length >= 2)
            {
                return Convert.ToInt32(hex.Substring(0, 2), 16) - 40;
            }
        }
        catch { }
        return 0;
    }

    private string CleanResponse(string raw)
    {
        string[] parts = raw.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string hexData = "";
        bool startCollection = false;
        
        foreach (var part in parts)
        {
            if (part == "41") startCollection = true;
            if (startCollection && part != "41" && part.Length == 2) 
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(part, "\\A\\b[0-9a-fA-F]+\\b\\Z"))
                {
                    hexData += part;
                }
            }
        }
        
        if (string.IsNullOrEmpty(hexData) && raw.Length > 4) return raw.Replace(" ", ""); 
        
        return hexData;
    }

    private ObdData GenerateMockData()
    {
        _mockRpm += _random.Next(-50, 150); 
        if (_mockRpm < 800) _mockRpm = 800 + _random.Next(0, 50);
        if (_mockRpm > 6000) _mockRpm = 5900;

        _mockSpeed = (int)(_mockRpm * 0.025); 
        if (_mockSpeed < 0) _mockSpeed = 0;

        _mockTemp += _random.Next(-1, 2);
        if (_mockTemp < 80) _mockTemp = 80;
        if (_mockTemp > 110) _mockTemp = 105;

        return new ObdData
        {
            Rpm = Math.Round(_mockRpm, 0),
            Speed = _mockSpeed,
            CoolantTemp = _mockTemp,
            BatteryVoltage = 13.5 + (_random.NextDouble() * 0.5),
            ConnectionStatus = "Connected (Simulation)",
            LastMessage = "Simulated Data OK"
        };
    }
}