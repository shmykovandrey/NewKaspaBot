using FluentResults;
using KaspaBot.Application.Trading.Commands;
using MediatR;
using Mexc.Net.Enums;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace KaspaBot.Presentation.Telegram.CommandHandlers
{
    public class TradingCommandHandler
    {
        private readonly IMediator _mediator;
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<TradingCommandHandler> _logger;

        public TradingCommandHandler(
            IMediator mediator,
            ITelegramBotClient botClient,
            ILogger<TradingCommandHandler> logger)
        {
            _mediator = mediator;
            _botClient = botClient;
            _logger = logger;
        }

        public async Task HandleBuyCommand(Message message, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _mediator.Send(new PlaceOrderCommand(
                    message.Chat.Id,
                    "KASUSDT",
                    OrderSide.Buy,
                    OrderType.Market,
                    Amount: 1m), cancellationToken);

                var response = result.IsSuccess
                    ? $"✅ Order placed: {result.Value.Id}"
                    : $"❌ Error: {string.Join(", ", result.Errors)}";

                await _botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: response,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling buy command");
                await _botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: $"❌ Unexpected error: {ex.Message}",
                    cancellationToken: cancellationToken);
            }
        }
    }
}