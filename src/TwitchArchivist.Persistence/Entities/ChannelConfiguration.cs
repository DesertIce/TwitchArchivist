namespace TwitchArchivist.Persistence.Entities;

public class ChannelConfiguration
{
    public int Id { get; set; }

    public string TwitchLogin { get; set; } = string.Empty;

    public string? TwitchUserId { get; set; }

    public string OutputDirectory { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public ICollection<EventSubscriptionState> EventSubscriptions { get; set; } = [];

    public ICollection<ArchiveJob> ArchiveJobs { get; set; } = [];

    public StreamSessionState? StreamSessionState { get; set; }
}
