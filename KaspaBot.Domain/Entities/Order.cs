// KaspaBot.Domain/Entities/Order.cs
public class Order
{
    public string Id { get; set; }
    public string Symbol { get; set; }
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// KaspaBot.Domain/Entities/OrderPair.cs
public class OrderPair
{
    public string Id { get; set; }
    public Order BuyOrder { get; set; }
    public Order SellOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}