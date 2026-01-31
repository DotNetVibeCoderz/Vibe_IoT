namespace OBDReader.Models;

public class ObdData
{
    public double Rpm { get; set; }
    public int Speed { get; set; }
    public int CoolantTemp { get; set; }
    public double BatteryVoltage { get; set; }
    public string ConnectionStatus { get; set; } = "Disconnected";
    public string LastMessage { get; set; } = "";
}