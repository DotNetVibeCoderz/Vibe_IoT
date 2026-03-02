using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HomeDashboard.Models
{
    public class EnergyLog
    {
        [Key]
        public int Id { get; set; }
        public int DeviceId { get; set; }
        [ForeignKey("DeviceId")]
        public Device Device { get; set; } = null!;

        public double ConsumedKWh { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}