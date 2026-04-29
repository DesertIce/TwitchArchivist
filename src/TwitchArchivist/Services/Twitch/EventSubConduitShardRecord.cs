namespace TwitchArchivist.Services.Twitch;

public sealed record EventSubConduitShardRecord(
    string ShardId,
    string Status,
    string? TransportSessionId);
