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
            return $"<b>✅ ПРОДАНО</b>\n{qty:F2} KAS по {price:F6} USDT\n\n<b>💰 Получено</b>\n{usdt:F8} USDT\n\n<b>📈 ПРИБЫЛЬ</b>\n{profit:F8} USDT";
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
                header = (!isStartup || lastBuyPrice.HasValue) ? "<b>Автопокупка совершена</b>" : "<b>🚀 Старт автоторговли</b>";
            }

            return $"{header}\n\n✅ <b>КУПЛЕНО</b>\n📊 <b>{buyQty:F2} KAS</b> по <b>{buyPrice:F6} USDT</b>\n\n💰 <b>Потрачено:</b> <b>{buyQty * buyPrice:F8} USDT</b>\n\n📈 <b>ВЫСТАВЛЕНО</b>\n📊 <b>{sellQty:F2} KAS</b> по <b>{sellPrice:F6} USDT</b>";
        }

        public static string StatTable(IEnumerable<(int Index, decimal Qty, decimal Price, decimal Sum, decimal Deviation)> rows, decimal totalSum, decimal currentPrice, string autotradeStatus, string autoBuyInfo, int totalCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<b>{autotradeStatus}</b>");
            sb.AppendLine("<b>🚀 Ордера на продажу</b>");
            sb.AppendLine($"📊 <b>Общее количество ордеров:</b> {totalCount}");
            sb.AppendLine($"💰 <b>Общая сумма всех ордеров:</b> {totalSum:F2}");
            sb.AppendLine();
            sb.AppendLine("<pre>");
            sb.AppendLine(" # | Кол-во |  Цена  | Сумма | Отклон");
            sb.AppendLine("---|--------|--------|-------|--------");
            foreach (var row in rows)
            {
                sb.AppendLine($"{row.Index,2} | {row.Qty,6:F2} | {row.Price,6:F4} | {row.Sum,5:F2} | {row.Deviation,5:F2}%");
            }
            sb.AppendLine("</pre>");
            sb.AppendLine($"\n💵 <b>Текущая цена:</b> {currentPrice:F4}{autoBuyInfo}");
            return sb.ToString();
        }

        public static string ProfitTable(IEnumerable<(string Date, decimal Profit, int Count)> rows, decimal weekProfit, int weekCount, decimal allProfit, int allCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<b>📈 Полный профит</b>\n");
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
            sb.AppendLine(new string('-', maxLength) + "|--------|------");
            sb.AppendLine($"{string.Format(format, "За неделю")} | {weekProfit,6:F2} | {weekCount,4}");
            sb.AppendLine($"{string.Format(format, "За всё время")} | {allProfit,6:F2} | {allCount,4}");
            sb.AppendLine("</pre>");
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