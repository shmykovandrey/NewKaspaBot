using FluentResults;
using KaspaBot.Application.Trading.Commands;
using KaspaBot.Application.Trading.Dtos;
using KaspaBot.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace KaspaBot.Presentation.Telegram.CommandHandlers;

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

    public async Task HandleBuyCommand(Message message)
    {
        try
        {
            var result = await _mediator.Send(new PlaceOrderCommand(
                message.Chat.Id,
                "KASUSDT",
                OrderSide.Buy,
                OrderType.Market,
                Amount: 1m));

            if (result.IsSuccess)
            {
                await _botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    $"✅ Order placed: {result.Value.Id}");
            }
            else
            {
                await _botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    $"❌ Error: {string.Join(", ", result.Errors)}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling buy command");
            await _botClient.SendTextMessageAsync(
                message.Chat.Id,
                $"❌ Unexpected error: {ex.Message}");
        }
    }
}