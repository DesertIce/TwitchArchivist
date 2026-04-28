namespace TwitchArchivist.Services.Twitch;

public sealed record TwitchLiveStreamState(
    string BroadcasterUserId,
    string BroadcasterLogin,
    string StreamId,
    DateTimeOffset StartedAtUtc);
