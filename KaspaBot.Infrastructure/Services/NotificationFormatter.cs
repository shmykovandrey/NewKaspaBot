using System;
using System.Collections.Generic;
using System.Text;
using System.Linq; // Added for .Any() and .Max()

namespace KaspaBot.Infrastructure.Services
{
    public static class NotificationFormatter
    {
        public static string Profit(decimal qty, decimal price, decimal usdt, decimal profit)
        {
            return $"<b>✅ ПРОДАНО</b>\n{qty:F2} KAS по {price:F6} USDT\n\n" + "<b>\ud83d\udcb0 Получено</b>\n" + $"{usdt:F8} USDT\n\n" + "<b>\ud83d\udcc8 ПРИБЫЛЬ</b>\n" + $"{profit:F8} USDT";
        }

        public static string AutoBuy(decimal buyQty, decimal buyPrice, decimal sellQty, decimal sellPrice, decimal? lastBuyPrice = null, decimal? currentPrice = null, bool isStartup = false)
        {
            string header;
            if (lastBuyPrice.HasValue && currentPrice.HasValue)
            {
                if (buyPrice < lastBuyPrice.Value)
                {
                    decimal dropPercent = 100m * (lastBuyPrice.Value - buyPrice) / lastBuyPrice.Value;
                    header = $"<b>Цена упала на {dropPercent:F2}%: {lastBuyPrice:F6} → {buyPrice:F6} USDT</b>";
                }
                else
                {
                    header = "<b>Автопокупка совершена</b>";
                }
            }
            else
            {
                header = (!isStartup || lastBuyPrice.HasValue) ? "<b>Автопокупка совершена</b>" : "<b>\ud83d\ude80 Старт автоторговли</b>";
            }

            return $"{header}\n\n✅ <b>КУПЛЕНО</b>\n\ud83d\udcca <b>{buyQty:F2} KAS</b> по <b>{buyPrice:F6} USDT</b>\n\n\ud83d\udcb0 <b>Потрачено:</b> <b>{buyQty * buyPrice:F8} USDT</b>\n\n\ud83d\udcc8 <b>ВЫСТАВЛЕНО</b>\n\ud83d\udcca <b>{sellQty:F2} KAS</b> по <b>{sellPrice:F6} USDT</b>";
        }

        public static string StatTable(IEnumerable<(int Index, decimal Qty, decimal Price, decimal Sum, decimal Deviation)> rows, decimal totalSum, decimal currentPrice, string autotradeStatus, string autoBuyInfo, int totalCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<b>{autotradeStatus}</b>");
            sb.AppendLine("<b>\ud83d\ude80 Ордера на продажу</b>");
            sb.AppendLine($"\ud83d\udcca <b>Общее количество ордеров:</b> {totalCount}");
            sb.AppendLine($"\ud83d\udcb0 <b>Общая сумма всех ордеров:</b> {totalSum:F2}");
            sb.AppendLine();
            sb.AppendLine("<pre>");
            sb.AppendLine(" # | Кол-во |  Цена  | Сумма | Отклон");
            sb.AppendLine("---|--------|--------|-------|--------");
            foreach (var row in rows)
            {
                sb.AppendLine($"{row.Index,2} | {row.Qty,6:F2} | {row.Price,6:F4} | {row.Sum,5:F2} | {row.Deviation,5:F2}%");
            }
            sb.AppendLine("</pre>");
            sb.AppendLine($"\n\ud83d\udcb5 <b>Текущая цена:</b> {currentPrice:F4}{autoBuyInfo}");
            return sb.ToString();
        }

        public static string ProfitTable(IEnumerable<(string Date, decimal Profit, int Count)> rows, decimal weekProfit, int weekCount, decimal allProfit, int allCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<b>\ud83d\udcc8 Полный профит</b>\n");
            sb.AppendLine("<pre>");
            string[] headers = { "Дата", "За неделю", "За всё время" };
            int maxLength = rows.Any() ? Math.Max(rows.Max(r => r.Date.Length), headers.Max(h => h.Length)) : headers.Max(h => h.Length);
            string format = $"{{0,-{maxLength}}}";
            sb.AppendLine($"{string.Format(format, "Дата")} | Профит | Сдел");
            sb.AppendLine(new string('-', maxLength) + "|--------|------");
            foreach (var row in rows)
            {
                sb.AppendLine($"{string.Format(format, row.Date)} | {row.Profit,6:F2} | {row.Count,4}");
            }
            sb.AppendLine("</pre>");
            sb.AppendLine($"\n<b>За неделю:</b> {weekProfit:F2} USDT ({weekCount} сделок)");
            sb.AppendLine($"<b>За всё время:</b> {allProfit:F2} USDT ({allCount} сделок)");
            return sb.ToString();
        }

        public static string BalanceTable(IEnumerable<(string Asset, decimal Total, decimal Available, decimal Frozen, decimal? UsdtValue)> rows, decimal totalUsdt)
        {
            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                sb.AppendLine($"<b>Баланс {row.Asset}:</b>");
                sb.AppendLine($"Total: <b>{row.Total:F2}</b> Free:<b>{row.Available:F2}</b> Locked:<b>{row.Frozen:F2}</b>");
                sb.AppendLine();
            }
            sb.AppendLine("================================================");
            sb.AppendLine("<b>Всего активов USDT по текущей цене KAS=0.096872:</b>");
            var usdtRows = rows.Where(r => r.Asset == "USDT");
            decimal usdtAvailable = usdtRows.Sum(r => r.Available);
            decimal usdtFrozen = usdtRows.Sum(r => r.Frozen);
            sb.AppendLine($"Total: <b>{totalUsdt:F2}</b> Free:<b>{usdtAvailable:F2}</b> Locked:<b>{usdtFrozen:F2}</b>");
            return sb.ToString();
        }
    }
} 