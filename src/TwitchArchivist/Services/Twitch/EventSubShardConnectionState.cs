namespace TwitchArchivist.Services.Twitch;

public sealed record EventSubShardConnectionState(
    string? SessionId,
    bool IsConnected,
    DateTimeOffset? LastWelcomeUtc,
    DateTimeOffset? LastReconnectedUtc,
    DateTimeOffset? LastDisconnectedUtc);
