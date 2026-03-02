using System;
using System.ComponentModel.DataAnnotations;

namespace HomeDashboard.Models
{
    public class NewsItem
    {
        [Key]
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public DateTime PublishedAt { get; set; } = DateTime.Now;
    }
}