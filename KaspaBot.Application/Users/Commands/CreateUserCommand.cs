using KaspaBot.Application.Users.Dtos;
using MediatR;

namespace KaspaBot.Application.Users.Commands;

public record CreateUserCommand(long UserId, string Username) : IRequest<UserDto>;