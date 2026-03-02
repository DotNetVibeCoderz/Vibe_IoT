using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HomeDashboard.Models
{
    public class Device
    {
        [Key]
        public int Id { get; set; }
        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty; // Smart Light, Camera, AC
        
        [MaxLength(50)]
        public string Type { get; set; } = "Light"; // Light, Camera, Sensor, AC, Lock
        
        [MaxLength(50)]
        public string Protocol { get; set; } = "Wi-Fi"; // Wi-Fi, Zigbee, Z-Wave, MQTT
        
        public bool IsOn { get; set; } = false;
        public double PowerConsumption { get; set; } = 0.0; // in Watts
        public string StatusValue { get; set; } = string.Empty; // e.g., "24°C" or "Locked"

        public int RoomId { get; set; }
        [ForeignKey("RoomId")]
        public Room Room { get; set; } = null!;
    }
}