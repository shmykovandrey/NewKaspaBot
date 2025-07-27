namespace KaspaBot.Domain.ValueObjects;

public enum OrderAmountMode
{
    Fixed,
    Dynamic
}

public class UserSettings
{
    private decimal _dynamicOrderCoef = 40m;
    
    public decimal PercentPriceChange { get; set; } = 0.5m;
    public decimal PercentProfit { get; set; } = 0.5m;
    public decimal MaxUsdtUsing { get; set; } = 50m;
    public decimal OrderAmount { get; set; } = 1m;
    public bool EnableAutoTrading { get; set; } = false;
    public bool IsAutoTradeEnabled { get; set; } = false;
    public decimal? LastDcaBuyPrice { get; set; }
    public OrderAmountMode OrderAmountMode { get; set; } = OrderAmountMode.Fixed;
    
    public decimal DynamicOrderCoef
    {
        get
        {
            if (_dynamicOrderCoef < 1m)
                return 40m;
            return _dynamicOrderCoef;
        }
        set
        {
            _dynamicOrderCoef = value < 1m ? 40m : value;
        }
    }
    
    public decimal GetOrderAmount(decimal usdtBalance)
    {
        decimal coef = _dynamicOrderCoef < 1m ? 1m : _dynamicOrderCoef;
        
        if (OrderAmountMode == OrderAmountMode.Fixed)
        {
            return OrderAmount;
        }
        
        decimal dynamicAmount = usdtBalance / coef;
        return Math.Max(1m, Math.Min(dynamicAmount, MaxUsdtUsing));
    }
}