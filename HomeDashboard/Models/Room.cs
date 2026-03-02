using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HomeDashboard.Models
{
    public class Room
    {
        [Key]
        public int Id { get; set; }
        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty; // Living Room, Kitchen, etc.
        public string Description { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty; // For background
        
        public ICollection<Device> Devices { get; set; } = new List<Device>();
    }
}