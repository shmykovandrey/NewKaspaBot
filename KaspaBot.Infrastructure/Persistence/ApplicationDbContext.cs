using KaspaBot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KaspaBot.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<OrderPair> OrderPairs { get; set; }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.OwnsOne(u => u.Settings, settings =>
            {
                settings.Property(s => s.EnableAutoTrading).HasColumnName("Settings_EnableAutoTrading");
            });
            entity.OwnsOne(u => u.ApiCredentials);
        });
        modelBuilder.Entity<OrderPair>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.UserId);
            entity.OwnsOne(p => p.BuyOrder);
            entity.OwnsOne(p => p.SellOrder);
        });
    }
}