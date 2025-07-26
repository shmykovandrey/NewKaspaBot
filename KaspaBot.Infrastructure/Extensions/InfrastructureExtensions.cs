using CryptoExchange.Net.Authentication;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Infrastructure.Options;
using KaspaBot.Infrastructure.Services;
using KaspaBot.Infrastructure.Repositories;
using KaspaBot.Infrastructure.Persistence;
using MediatR;
using Mexc.Net.Clients;
using Mexc.Net.Interfaces.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KaspaBot.Infrastructure.Extensions
{
    public static class InfrastructureExtensions
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
        {
            // Регистрация MexcOptions
            services.Configure<MexcOptions>(configuration.GetSection(MexcOptions.SectionName));

            // Регистрация MexcRestClient
            services.AddSingleton<IMexcRestClient>(provider =>
            {
                var options = configuration.GetSection(MexcOptions.SectionName).Get<MexcOptions>();
                if (options == null || string.IsNullOrEmpty(options.ApiKey) || string.IsNullOrEmpty(options.ApiSecret))
                {
                    throw new InvalidOperationException("Mexc API credentials are not configured properly");
                }

                var client = new MexcRestClient();
                client.SetApiCredentials(new ApiCredentials(options.ApiKey, options.ApiSecret));
                return client;
            });

            // Регистрация сервисов
            // services.AddScoped<IMexcService, MexcService>();
            services.AddScoped<IPriceStreamService, PriceStreamService>();
            services.AddScoped<IUserRepository, UserRepository>();
            services.AddScoped<OrderPairRepository>();
            services.AddSingleton<EncryptionService>();
            services.AddSingleton<UserStreamManager>(provider =>
            {
                var userRepo = provider.GetRequiredService<IUserRepository>();
                var logger = provider.GetRequiredService<ILogger<UserStreamManager>>();
                var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
                var orderPairRepo = provider.GetRequiredService<OrderPairRepository>();
                var serviceProvider = provider;
                var orderRecovery = provider.GetRequiredService<KaspaBot.Domain.Interfaces.IOrderRecoveryService>();
                var botMessenger = provider.GetRequiredService<KaspaBot.Domain.Interfaces.IBotMessenger>();
                return new UserStreamManager(userRepo, logger, loggerFactory, orderPairRepo, serviceProvider, orderRecovery, botMessenger);
            });

            // Регистрация MediatR (упрощенная версия)
            services.AddMediatR(typeof(InfrastructureExtensions).Assembly);

            // Регистрация ApplicationDbContext
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(configuration.GetConnectionString("DefaultConnection")));

            return services;
        }
    }
}