namespace TwitchArchivist.Services.Twitch;

public sealed record EventSubSubscriptionRecord(
    string Id,
    string Type,
    string Status,
    string? BroadcasterUserId,
    string? TransportSessionId,
    string? TransportConduitId);
