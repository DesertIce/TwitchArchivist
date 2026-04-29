namespace TwitchArchivist.Persistence.Entities;

public class EventSubConduit
{
    public int Id { get; set; }

    public string TwitchConduitId { get; set; } = string.Empty;

    public int ShardCount { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public ICollection<EventSubConduitShard> Shards { get; set; } = [];

    public ICollection<EventSubSubscriptionBinding> SubscriptionBindings { get; set; } = [];
}
