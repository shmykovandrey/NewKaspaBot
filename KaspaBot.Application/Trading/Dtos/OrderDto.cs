using KaspaBot.Domain.Entities;

namespace KaspaBot.Application.Trading.Dtos;

public class OrderDto
{
    public string Id { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public OrderDto(Order order)
    {
        Id = order.Id;
        Symbol = order.Symbol;
        Side = order.Side.ToString();
        Type = order.Type.ToString();
        Quantity = order.Quantity;
        Price = order.Price;
        Status = order.Status.ToString();
        CreatedAt = order.CreatedAt;
    }
}