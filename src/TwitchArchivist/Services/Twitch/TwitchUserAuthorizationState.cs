namespace TwitchArchivist.Services.Twitch;

public sealed record TwitchUserAuthorizationState(
    bool IsConfigured,
    bool IsValid,
    string Validity,
    string? Detail,
    bool HasRefreshToken,
    string? TwitchUserId,
    string? TwitchUserLogin,
    DateTimeOffset? ExpiresUtc,
    DateTimeOffset? LastValidatedUtc);
