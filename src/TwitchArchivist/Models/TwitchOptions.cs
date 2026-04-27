namespace TwitchArchivist.Models;

public class TwitchOptions
{
    public const string SectionName = "Twitch";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public int AppAccessTokenRefreshBufferMinutes { get; set; } = 5;

    public int AppAccessTokenRefreshPollingIntervalSeconds { get; set; } = 60;

    public int VodDiscoveryInitialDelaySeconds { get; set; } = 30;

    public int VodDiscoveryRetryCount { get; set; } = 20;

    public int VodDiscoveryRetryDelaySeconds { get; set; } = 30;
}
