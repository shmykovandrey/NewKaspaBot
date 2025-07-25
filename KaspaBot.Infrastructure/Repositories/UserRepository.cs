using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using KaspaBot.Infrastructure.Services;

namespace KaspaBot.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly ApplicationDbContext _context;
    private readonly EncryptionService _encryptionService;

    public UserRepository(ApplicationDbContext context, EncryptionService encryptionService)
    {
        _context = context;
        _encryptionService = encryptionService;
    }

    public async Task<User?> GetByIdAsync(long userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user != null)
        {
            user.ApiCredentials.ApiKey = _encryptionService.Decrypt(user.ApiCredentials.ApiKey);
            user.ApiCredentials.ApiSecret = _encryptionService.Decrypt(user.ApiCredentials.ApiSecret);
        }
        return user;
    }

    public async Task AddAsync(User user)
    {
        if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiKey))
            user.ApiCredentials.ApiKey = _encryptionService.Encrypt(user.ApiCredentials.ApiKey);
        if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiSecret))
            user.ApiCredentials.ApiSecret = _encryptionService.Encrypt(user.ApiCredentials.ApiSecret);
        await _context.Users.AddAsync(user);
        await _context.SaveChangesAsync();
        user.ApiCredentials.ApiKey = _encryptionService.Decrypt(user.ApiCredentials.ApiKey);
        user.ApiCredentials.ApiSecret = _encryptionService.Decrypt(user.ApiCredentials.ApiSecret);
    }

    public async Task UpdateAsync(User user)
    {
        if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiKey))
            user.ApiCredentials.ApiKey = _encryptionService.Encrypt(user.ApiCredentials.ApiKey);
        if (!_encryptionService.IsEncrypted(user.ApiCredentials.ApiSecret))
            user.ApiCredentials.ApiSecret = _encryptionService.Encrypt(user.ApiCredentials.ApiSecret);
        _context.Users.Update(user);
        await _context.SaveChangesAsync();
        user.ApiCredentials.ApiKey = _encryptionService.Decrypt(user.ApiCredentials.ApiKey);
        user.ApiCredentials.ApiSecret = _encryptionService.Decrypt(user.ApiCredentials.ApiSecret);
    }

    public async Task<List<User>> GetAllAsync()
    {
        var users = await _context.Users.ToListAsync();
        foreach (var user in users)
        {
            user.ApiCredentials.ApiKey = _encryptionService.Decrypt(user.ApiCredentials.ApiKey);
            user.ApiCredentials.ApiSecret = _encryptionService.Decrypt(user.ApiCredentials.ApiSecret);
        }
        return users;
    }

    public async Task<bool> ExistsAsync(long userId)
    {
        return await _context.Users.AnyAsync(u => u.Id == userId);
    }

    public async Task DeleteAsync(long userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user != null)
        {
            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
        }
    }
}