namespace KaspaBot.Domain.ValueObjects;

public class UserApiCredentials
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
}