using KaspaBot.Domain.Entities;
using Mexc.Net.Enums;

namespace KaspaBot.Application.Trading.Dtos;

public class OrderDto
{
    public string Id { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    public OrderDto(Order order)
    {
        Id = order.Id;
        Symbol = order.Symbol;
        Side = order.Side;
        Type = order.Type;
        Quantity = order.Quantity;
        Price = order.Price;
        Status = order.Status;
        CreatedAt = order.CreatedAt;
    }
}