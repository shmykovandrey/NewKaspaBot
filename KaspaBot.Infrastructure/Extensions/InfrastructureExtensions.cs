using CryptoExchange.Net.Authentication;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Infrastructure.Options;
using KaspaBot.Infrastructure.Services;
using MediatR;
using Mexc.Net.Clients;
using Mexc.Net.Interfaces.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
            services.AddScoped<IMexcService, MexcService>();
            services.AddScoped<IPriceStreamService, PriceStreamService>();

            // Регистрация MediatR (упрощенная версия)
            services.AddMediatR(typeof(InfrastructureExtensions).Assembly);

            return services;
        }
    }
}