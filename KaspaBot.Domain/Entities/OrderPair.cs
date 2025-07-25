namespace KaspaBot.Domain.Entities;

public class OrderPair
{
    public required string Id { get; set; }
    public required Order BuyOrder { get; set; }
    public required Order SellOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public decimal? Profit { get; set; }
    public long UserId { get; set; }
}