namespace TwitchArchivist.Services.Twitch;

public interface ITwitchAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
}
