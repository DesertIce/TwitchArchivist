namespace TwitchArchivist.Persistence.Entities;

public class EventSubscriptionState
{
    public int Id { get; set; }

    public int ChannelConfigurationId { get; set; }

    public string SubscriptionType { get; set; } = string.Empty;

    public string? TwitchSubscriptionId { get; set; }

    public string? TransportSessionId { get; set; }

    public string Status { get; set; } = "unknown";

    public DateTimeOffset? LastVerifiedUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public ChannelConfiguration ChannelConfiguration { get; set; } = null!;
}
