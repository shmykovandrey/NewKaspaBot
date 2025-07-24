using Mexc.Net.Enums;
using Mexc.Net.Objects.Models.Spot;

namespace KaspaBot.Domain.Entities;

public class Order
{
    public required string Id { get; set; }
    public required string Symbol { get; set; }
    public OrderSide Side { get; set; } // Используем OrderSide из Mexc.Net
    public OrderType Type { get; set; } // Используем OrderType из Mexc.Net
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }
    public OrderStatus Status { get; set; } // Используем OrderStatus из Mexc.Net
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public decimal QuantityFilled { get; set; }
    public decimal QuoteQuantityFilled { get; set; }
    public decimal Commission { get; set; }
}