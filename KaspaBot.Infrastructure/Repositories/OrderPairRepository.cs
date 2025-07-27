using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KaspaBot.Domain.Entities;
using KaspaBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Infrastructure.Repositories
{
    public class OrderPairRepository
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<OrderPairRepository> _logger;
        public OrderPairRepository(ApplicationDbContext context, ILogger<OrderPairRepository> logger)
        {
            _context = context;
            _logger = logger;
        }
        public async Task AddAsync(OrderPair pair)
        {
            await _context.OrderPairs.AddAsync(pair);
            await _context.SaveChangesAsync();
        }
        public async Task UpdateAsync(OrderPair pair)
        {
            _context.OrderPairs.Update(pair);
            await _context.SaveChangesAsync();
        }
        public async Task<OrderPair?> GetByIdAsync(string id)
        {
            return await _context.OrderPairs.FindAsync(id);
        }
        public async Task<List<OrderPair>> GetOpenByUserIdAsync(long userId)
        {
            return await _context.OrderPairs.Where(p => p.UserId == userId && p.CompletedAt == null).ToListAsync();
        }
        public async Task<List<OrderPair>> GetAllAsync()
        {
            return await _context.OrderPairs.ToListAsync();
        }
        public async Task DeleteByIdAsync(string id)
        {
            var pair = await _context.OrderPairs.FindAsync(id);
            if (pair != null)
            {
                _context.OrderPairs.Remove(pair);
                await _context.SaveChangesAsync();
            }
        }
        public async Task DeleteByUserId(long userId)
        {
            var pairs = await _context.OrderPairs.Where(p => p.UserId == userId).ToListAsync();
            _logger.LogInformation($"[ORDERPAIR-DELETE] userId={userId} найдено пар: {pairs.Count}");
            _context.OrderPairs.RemoveRange(pairs);
            var deleted = await _context.SaveChangesAsync();
            _logger.LogInformation($"[ORDERPAIR-DELETE] userId={userId} удалено записей: {deleted}");
        }

        public async Task<Order?> FindOrderByIdAsync(string orderId)
        {
            var buyOrder = await _context.OrderPairs
                .Where(p => p.BuyOrder.Id == orderId)
                .Select(p => p.BuyOrder)
                .FirstOrDefaultAsync();

            if (buyOrder != null)
                return buyOrder;

            var sellOrder = await _context.OrderPairs
                .Where(p => p.SellOrder.Id == orderId)
                .Select(p => p.SellOrder)
                .FirstOrDefaultAsync();

            return sellOrder;
        }

        public async Task<(OrderPair, Order)?> FindOrderAndPairByOrderIdAsync(string orderId)
        {
            var pairWithBuyOrder = await _context.OrderPairs
                .Where(p => p.BuyOrder.Id == orderId)
                .Select(p => new { Pair = p, Order = p.BuyOrder })
                .FirstOrDefaultAsync();

            if (pairWithBuyOrder != null)
                return (pairWithBuyOrder.Pair, pairWithBuyOrder.Order);

            var pairWithSellOrder = await _context.OrderPairs
                .Where(p => p.SellOrder.Id == orderId)
                .Select(p => new { Pair = p, Order = p.SellOrder })
                .FirstOrDefaultAsync();

            if (pairWithSellOrder != null)
                return (pairWithSellOrder.Pair, pairWithSellOrder.Order);

            return null;
        }
    }
} 