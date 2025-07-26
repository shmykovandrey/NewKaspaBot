using KaspaBot.Domain.Entities;

namespace KaspaBot.Domain.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(long userId);
    Task AddAsync(User user);
    Task UpdateAsync(User user);
    Task<List<User>> GetAllAsync();
    Task<bool> ExistsAsync(long userId);
    Task DeleteAsync(long userId);
}