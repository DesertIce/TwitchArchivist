namespace TwitchArchivist.Persistence.Entities;

public class TwitchOAuthToken
{
    public int Id { get; set; }

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public string TokenType { get; set; } = "bearer";

    public string Scope { get; set; } = string.Empty;

    public string TwitchUserId { get; set; } = string.Empty;

    public string TwitchUserLogin { get; set; } = string.Empty;

    public DateTimeOffset ExpiresUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
