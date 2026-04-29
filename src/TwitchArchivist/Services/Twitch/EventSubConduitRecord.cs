namespace TwitchArchivist.Services.Twitch;

public sealed record EventSubConduitRecord(
    string Id,
    int ShardCount,
    IReadOnlyList<EventSubConduitShardRecord> Shards);
