using OBDReader.Models;
using System.Threading.Tasks;

namespace OBDReader.Services;

public interface IObdService
{
    bool IsConnected { get; }
    Task<bool> ConnectAsync(string portName, int baudRate);
    void Disconnect();
    Task<ObdData> ReadDataAsync();
    string[] GetAvailablePorts();
}