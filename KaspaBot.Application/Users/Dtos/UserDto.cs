using KaspaBot.Domain.Entities;

namespace KaspaBot.Application.Users.Dtos;

public class UserDto
{
    public long Id { get; set; }
    public string Username { get; set; }

    public UserDto(User user)
    {
        Id = user.Id;
        Username = user.Username;
    }
}