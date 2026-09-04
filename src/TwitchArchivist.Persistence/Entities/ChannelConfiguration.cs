namespace TwitchArchivist.Persistence.Entities;

public class ChannelConfiguration
{
    public int Id { get; set; }

    public string TwitchLogin { get; set; } = string.Empty;

    public string? Alias { get; set; }

    public string? TwitchUserId { get; set; }

    public string OutputDirectory { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public bool AutoPruneEnabled { get; set; }

    public int AutoPruneVodCount { get; set; } = 10;

    public bool CompressEnabled { get; set; }

    public int CompressVodCount { get; set; } = 10;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public ICollection<EventSubscriptionState> EventSubscriptions { get; set; } = [];

    public ICollection<ArchiveJob> ArchiveJobs { get; set; } = [];

    public StreamSessionState? StreamSessionState { get; set; }
}
