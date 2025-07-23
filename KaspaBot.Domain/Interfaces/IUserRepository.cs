using KaspaBot.Domain.Entities;

namespace KaspaBot.Domain.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(long userId);
    Task AddAsync(User user);
    Task UpdateAsync(User user);
}