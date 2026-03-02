using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using HomeDashboard.Data;
using HomeDashboard.Models;

namespace HomeDashboard.Services
{
    public class SimulatorService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly Random _random = new Random();

        public event Action? OnSimulationTicked;

        public SimulatorService(IServiceScopeFactory scopeFactory, IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Initial delay to let DB create
            await Task.Delay(5000, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var mode = _configuration.GetValue<string>("AppMode") ?? "Real";
                if (mode == "Simulator")
                {
                    await SimulateDataAsync();
                    OnSimulationTicked?.Invoke();
                }
                
                await Task.Delay(5000, stoppingToken); // Update every 5 seconds
            }
        }

        private async Task SimulateDataAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // 1. Simulate Device Power & Status
                var devices = db.Devices.ToList();
                foreach (var dev in devices)
                {
                    if (dev.IsOn)
                    {
                        // Randomize power consumption slightly
                        if (dev.Type == "AC")
                        {
                            dev.PowerConsumption = 800 + Math.Round(_random.NextDouble() * 200, 1);
                            dev.StatusValue = $"{20 + _random.Next(0, 6)}°C";
                        }
                        else if (dev.Type == "Appliance")
                        {
                            dev.PowerConsumption = 100 + Math.Round(_random.NextDouble() * 50, 1);
                        }
                        else if (dev.Type == "Light")
                        {
                            dev.PowerConsumption = 10 + Math.Round(_random.NextDouble() * 5, 1);
                        }
                        else if (dev.Type == "Camera")
                        {
                            dev.PowerConsumption = 5 + Math.Round(_random.NextDouble() * 2, 1);
                            dev.StatusValue = _random.NextDouble() > 0.8 ? "Detecting Motion..." : "Recording";
                        }
                    }
                    else
                    {
                        dev.PowerConsumption = 0;
                        if (dev.Type == "Camera") dev.StatusValue = "Offline";
                    }
                }

                // 2. Simulate Stock Prices
                var stocks = db.StockInfos.ToList();
                foreach (var stock in stocks)
                {
                    var change = Math.Round((_random.NextDouble() * 2) - 1, 2); // -1 to +1
                    stock.Price += change;
                    stock.IsUp = change >= 0;
                }

                // 3. Randomly insert a MotionLog occasionally
                if (_random.Next(0, 4) == 0) // 25% chance every 5 seconds
                {
                    var locations = new[] { "Front Door", "Driveway", "Backyard", "Living Room" };
                    var sevs = new[] { "Warning", "Info", "Error" };
                    db.MotionLogs.Add(new MotionLog 
                    { 
                        Message = $"Motion detected at {locations[_random.Next(locations.Length)]}",
                        Severity = sevs[_random.Next(sevs.Length)],
                        Timestamp = DateTime.Now
                    });

                    // Keep log size manageable
                    if (db.MotionLogs.Count() > 50)
                    {
                        var oldest = db.MotionLogs.OrderBy(m => m.Timestamp).First();
                        db.MotionLogs.Remove(oldest);
                    }
                }

                await db.SaveChangesAsync();
            }
            catch
            {
                // Ignore any DB lock issues gracefully in prototype
            }
        }
    }
}