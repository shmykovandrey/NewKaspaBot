using KaspaBot.Domain.Enums;

namespace KaspaBot.Domain.Entities;

public class Order
{
    public required string Id { get; set; }
    public required string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public decimal QuantityFilled { get; set; }
    public decimal QuoteQuantityFilled { get; set; }
    public decimal Commission { get; set; }
}