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
    }
} 