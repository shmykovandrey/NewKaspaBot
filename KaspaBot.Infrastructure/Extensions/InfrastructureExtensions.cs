using KaspaBot.Application.Trading.Handlers;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Infrastructure.Persistence;
using KaspaBot.Infrastructure.Repositories;
using KaspaBot.Infrastructure.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaspaBot.Infrastructure.Extensions;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("DefaultConnection")));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IMexcService, MexcService>();
        services.AddScoped<IPriceStreamService, PriceStreamService>();

        // MediatR для CQRS
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<PlaceOrderCommandHandler>());

        return services;
    }
}
