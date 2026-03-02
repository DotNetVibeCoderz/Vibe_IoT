using System.ComponentModel.DataAnnotations;

namespace HomeDashboard.Models
{
    public class StockInfo
    {
        [Key]
        public int Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public double Price { get; set; }
        public bool IsUp { get; set; }
    }
}