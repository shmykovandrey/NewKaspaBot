using Mexc.Net.Enums;

namespace KaspaBot.Infrastructure.Services
{
    public class OrderAuditEvent
    {
        public long UserId { get; set; }
        public string OrderId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public OrderSide Side { get; set; }
        public decimal Qty { get; set; }
        public decimal Price { get; set; }
        public OrderStatus Status { get; set; }
    }
} 