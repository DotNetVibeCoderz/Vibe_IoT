using System;
using Microsoft.EntityFrameworkCore;
using HomeDashboard.Models;

namespace HomeDashboard.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Room> Rooms { get; set; } = null!;
        public DbSet<Device> Devices { get; set; } = null!;
        public DbSet<EnergyLog> EnergyLogs { get; set; } = null!;
        
        public DbSet<NewsItem> NewsItems { get; set; } = null!;
        public DbSet<StockInfo> StockInfos { get; set; } = null!;
        public DbSet<MotionLog> MotionLogs { get; set; } = null!;
        public DbSet<Scene> Scenes { get; set; } = null!;
        public DbSet<Routine> Routines { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            // Seed User
            modelBuilder.Entity<User>().HasData(
                new User { Id = 1, Username = "admin", Password = "admin123", Role = "admin", FullName = "Admin User", Email = "admin@homedash.com" },
                new User { Id = 2, Username = "user", Password = "user123", Role = "user", FullName = "Normal User", Email = "user@homedash.com" }
            );

            // Seed Rooms
            modelBuilder.Entity<Room>().HasData(
                new Room { Id = 1, Name = "Living Room", Description = "Main family area", ImageUrl = "images/livingroom.jpg" },
                new Room { Id = 2, Name = "Kitchen", Description = "Cooking and dining", ImageUrl = "images/kitchen.jpg" },
                new Room { Id = 3, Name = "Bedroom", Description = "Master bedroom", ImageUrl = "images/bedroom.jpg" },
                new Room { Id = 4, Name = "Garage", Description = "Car and storage", ImageUrl = "images/garage.jpg" }
            );

            // Seed Devices
            modelBuilder.Entity<Device>().HasData(
                new Device { Id = 1, Name = "Main Light", Type = "Light", Protocol = "Zigbee", IsOn = true, RoomId = 1, PowerConsumption = 15, StatusValue = "100%" },
                new Device { Id = 2, Name = "TV Console", Type = "Appliance", Protocol = "Wi-Fi", IsOn = true, RoomId = 1, PowerConsumption = 120, StatusValue = "Netflix" },
                new Device { Id = 3, Name = "Living AC", Type = "AC", Protocol = "Z-Wave", IsOn = false, RoomId = 1, PowerConsumption = 0, StatusValue = "24°C" },
                new Device { Id = 4, Name = "Security Cam 1", Type = "Camera", Protocol = "Wi-Fi", IsOn = true, RoomId = 1, PowerConsumption = 5, StatusValue = "Recording" },

                new Device { Id = 5, Name = "Smart Fridge", Type = "Appliance", Protocol = "Wi-Fi", IsOn = true, RoomId = 2, PowerConsumption = 200, StatusValue = "3°C" },
                new Device { Id = 6, Name = "Oven", Type = "Appliance", Protocol = "Zigbee", IsOn = false, RoomId = 2, PowerConsumption = 0, StatusValue = "Off" },
                new Device { Id = 7, Name = "Kitchen Light", Type = "Light", Protocol = "Zigbee", IsOn = false, RoomId = 2, PowerConsumption = 0, StatusValue = "Off" },

                new Device { Id = 8, Name = "Bed Light", Type = "Light", Protocol = "Zigbee", IsOn = true, RoomId = 3, PowerConsumption = 10, StatusValue = "50%" },
                new Device { Id = 9, Name = "Bedroom AC", Type = "AC", Protocol = "Wi-Fi", IsOn = true, RoomId = 3, PowerConsumption = 900, StatusValue = "22°C" },

                new Device { Id = 10, Name = "Garage Door Lock", Type = "Lock", Protocol = "Z-Wave", IsOn = true, RoomId = 4, PowerConsumption = 1, StatusValue = "Locked" },
                new Device { Id = 11, Name = "Driveway Cam", Type = "Camera", Protocol = "Wi-Fi", IsOn = true, RoomId = 4, PowerConsumption = 5, StatusValue = "Active" }
            );

            // Seed EnergyLogs (mock data)
            modelBuilder.Entity<EnergyLog>().HasData(
                new EnergyLog { Id = 1, DeviceId = 2, ConsumedKWh = 1.2, Timestamp = DateTime.Now.AddHours(-1) },
                new EnergyLog { Id = 2, DeviceId = 9, ConsumedKWh = 5.5, Timestamp = DateTime.Now.AddHours(-2) },
                new EnergyLog { Id = 3, DeviceId = 5, ConsumedKWh = 3.0, Timestamp = DateTime.Now.AddHours(-3) }
            );

            // Seed News
            modelBuilder.Entity<NewsItem>().HasData(
                new NewsItem { Id = 1, Title = "Tech giant releases new AI", Icon = "Article", PublishedAt = DateTime.Now.AddHours(-1) },
                new NewsItem { Id = 2, Title = "Stock markets hit all-time high", Icon = "TrendingUp", PublishedAt = DateTime.Now.AddHours(-3) }
            );

            // Seed Stocks
            modelBuilder.Entity<StockInfo>().HasData(
                new StockInfo { Id = 1, Symbol = "AAPL", Price = 150.25, IsUp = true },
                new StockInfo { Id = 2, Symbol = "MSFT", Price = 310.11, IsUp = false }
            );

            // Seed Motion Logs
            modelBuilder.Entity<MotionLog>().HasData(
                new MotionLog { Id = 1, Message = "Motion detected at Driveway", Severity = "Warning", Timestamp = DateTime.Now.AddMinutes(-10) },
                new MotionLog { Id = 2, Message = "Postman arrived", Severity = "Info", Timestamp = DateTime.Now.AddHours(-2) },
                new MotionLog { Id = 3, Message = "System Armed", Severity = "Success", Timestamp = DateTime.Now.AddHours(-8) }
            );

            // Seed Scenes
            modelBuilder.Entity<Scene>().HasData(
                new Scene { Id = 1, Name = "Good Morning", Description = "Blinds up, Coffee Maker On", Icon = "WbSunny", Color = "Warning" },
                new Scene { Id = 2, Name = "Movie Night", Description = "Lights 10%, TV On, AC 22°C", Icon = "Movie", Color = "Primary" },
                new Scene { Id = 3, Name = "Leave Home", Description = "All Off, Lock Doors, Arm Cam", Icon = "DirectionsWalk", Color = "Error" },
                new Scene { Id = 4, Name = "Good Night", Description = "Lights Off, Lock Doors, AC 24°C", Icon = "NightsStay", Color = "Dark" }
            );

            // Seed Routines
            modelBuilder.Entity<Routine>().HasData(
                new Routine { Id = 1, Name = "Turn on porch lights at sunset", Description = "If Time is Sunset, Then Turn On Front Light", IsActive = true },
                new Routine { Id = 2, Name = "Turn off AC when window opens", Description = "If Living Room Window Open, Then Turn Off AC", IsActive = true },
                new Routine { Id = 3, Name = "Wake Up Alarm", Description = "Every Weekday at 06:30 AM, play Spotify and open blinds", IsActive = false }
            );
        }
    }
}