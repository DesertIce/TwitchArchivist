namespace TwitchArchivist.Persistence.Entities;

public class EventSubSubscriptionBinding
{
    public int Id { get; set; }

    public int EventSubConduitId { get; set; }

    public int ChannelConfigurationId { get; set; }

    public string SubscriptionType { get; set; } = string.Empty;

    public string? TwitchSubscriptionId { get; set; }

    public string Status { get; set; } = "unknown";

    public DateTimeOffset? LastVerifiedUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public EventSubConduit EventSubConduit { get; set; } = null!;

    public ChannelConfiguration ChannelConfiguration { get; set; } = null!;
}
