using System;
using System.ComponentModel.DataAnnotations;

namespace HomeDashboard.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }
        [Required, MaxLength(50)]
        public string Username { get; set; } = string.Empty;
        [Required, MaxLength(100)]
        public string Password { get; set; } = string.Empty;
        [MaxLength(50)]
        public string Role { get; set; } = "user"; // admin, user
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}