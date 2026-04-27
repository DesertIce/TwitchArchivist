namespace TwitchArchivist.Services.Twitch;

public interface ITwitchAccessTokenProvider
{
    string? BuildUserAuthorizationUrl(string state, string redirectUri);

    Task ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken);

    Task<string?> GetAppAccessTokenAsync(CancellationToken cancellationToken);

    Task<string?> GetUserAccessTokenAsync(CancellationToken cancellationToken);

    Task<TwitchUserAuthorizationState> GetUserAuthorizationStateAsync(CancellationToken cancellationToken);

    Task<TwitchUserAuthorizationState> ValidateUserAuthorizationAsync(CancellationToken cancellationToken);
}
