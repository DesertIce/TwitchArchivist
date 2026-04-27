namespace TwitchArchivist.Persistence.Entities;

public class StreamSessionState
{
    public int Id { get; set; }

    public int ChannelConfigurationId { get; set; }

    public string? LastKnownStreamId { get; set; }

    public DateTimeOffset? LastOnlineUtc { get; set; }

    public DateTimeOffset? LastOfflineUtc { get; set; }

    public string? LastProcessedOfflineMessageId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public ChannelConfiguration ChannelConfiguration { get; set; } = null!;
}
