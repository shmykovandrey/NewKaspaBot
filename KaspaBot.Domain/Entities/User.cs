using KaspaBot.Domain.ValueObjects;

namespace KaspaBot.Domain.Entities;

public class User
{
    public long Id { get; set; }
    public string Username { get; set; }
    public DateTime RegistrationDate { get; set; }
    public UserSettings Settings { get; set; }
    public UserApiCredentials ApiCredentials { get; set; }
    public bool IsActive { get; set; }

    public User(long id, string username)
    {
        Id = id;
        Username = username;
        RegistrationDate = DateTime.UtcNow;
        Settings = new UserSettings();
        ApiCredentials = new UserApiCredentials();
        IsActive = true;
    }
}