using System;
using System.ComponentModel.DataAnnotations;

namespace HomeDashboard.Models
{
    public class MotionLog
    {
        [Key]
        public int Id { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Severity { get; set; } = "Info"; // Success, Warning, Info, Error
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}