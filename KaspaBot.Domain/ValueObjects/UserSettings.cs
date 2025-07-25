namespace KaspaBot.Domain.ValueObjects;

public class UserSettings
{
    public decimal PercentPriceChange { get; set; } = 0.5m;
    public decimal PercentProfit { get; set; } = 0.5m;
    public decimal MaxUsdtUsing { get; set; } = 50m;
    public decimal OrderAmount { get; set; } = 1m;
    public bool EnableAutoTrading { get; set; } = false;
    public bool IsAutoTradeEnabled { get; set; } = false;
    public decimal? LastDcaBuyPrice { get; set; }
}