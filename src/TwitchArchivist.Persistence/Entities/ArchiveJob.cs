namespace TwitchArchivist.Persistence.Entities;

public class ArchiveJob
{
    public int Id { get; set; }

    public int ChannelConfigurationId { get; set; }

    public string TriggerSource { get; set; } = string.Empty;

    public string? VodId { get; set; }

    public string? OutputPath { get; set; }

    public ArchiveJobStatus Status { get; set; } = ArchiveJobStatus.Pending;

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? CompletedUtc { get; set; }

    public ChannelConfiguration ChannelConfiguration { get; set; } = null!;
}
