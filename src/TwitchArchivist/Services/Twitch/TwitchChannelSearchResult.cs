namespace TwitchArchivist.Services.Twitch;

public sealed record TwitchChannelSearchResult(
    string UserId,
    string Login,
    string DisplayName,
    string ThumbnailUrl,
    bool IsLive);
