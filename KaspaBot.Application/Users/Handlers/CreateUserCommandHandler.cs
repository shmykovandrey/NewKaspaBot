using KaspaBot.Domain.Entities;
using KaspaBot.Domain.Interfaces;
using KaspaBot.Application.Users.Dtos;
using MediatR;
using KaspaBot.Application.Users.Commands;

namespace KaspaBot.Application.Users.Handlers;

public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, UserDto>
{
    private readonly IUserRepository _userRepository;

    public CreateUserCommandHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<UserDto> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var user = new User(request.UserId, request.Username);
        await _userRepository.AddAsync(user);
        return new UserDto(user);
    }
}