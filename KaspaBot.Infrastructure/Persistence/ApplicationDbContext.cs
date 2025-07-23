using KaspaBot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KaspaBot.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext
{
    public DbSet<User> Users { get; set; }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.OwnsOne(u => u.Settings);
            entity.OwnsOne(u => u.ApiCredentials);
        });
    }
}