namespace TwitchArchivist.Models;

public class TwitchOptions
{
    public const string SectionName = "Twitch";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public int AppAccessTokenRefreshBufferMinutes { get; set; } = 5;

    public int UserAccessTokenRefreshBufferMinutes { get; set; } = 15;

    public int AppAccessTokenRefreshPollingIntervalSeconds { get; set; } = 60;

    public int LiveStateFallbackPollingIntervalSeconds { get; set; } = 900;

    public int EventSubMonitorIntervalSeconds { get; set; } = 15;

    public int EventSubSubscriptionSyncIntervalSeconds { get; set; } = 60;

    public int EventSubRetryBaseDelaySeconds { get; set; } = 5;

    public int EventSubRetryMaxDelaySeconds { get; set; } = 60;

    public string EventSubTransportMode { get; set; } = "websocket";

    public int EventSubConduitShardCount { get; set; } = 4;

    public string? EventSubConduitId { get; set; }

    public int EventSubConduitAssignmentTimeoutSeconds { get; set; } = 10;

    public int EventSubConduitReconcileIntervalSeconds { get; set; } = 60;

    public int VodDiscoveryInitialDelaySeconds { get; set; } = 30;

    public int VodDiscoveryRetryCount { get; set; } = 20;

    public int VodDiscoveryRetryDelaySeconds { get; set; } = 30;
}
