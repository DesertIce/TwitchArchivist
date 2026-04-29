namespace TwitchArchivist.Persistence.Entities;

public class EventSubConduitShard
{
    public int Id { get; set; }

    public int EventSubConduitId { get; set; }

    public int ShardId { get; set; }

    public string? TransportSessionId { get; set; }

    public string Status { get; set; } = "unknown";

    public DateTimeOffset? LastWelcomeUtc { get; set; }

    public DateTimeOffset? LastAssignmentUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public EventSubConduit EventSubConduit { get; set; } = null!;
}
