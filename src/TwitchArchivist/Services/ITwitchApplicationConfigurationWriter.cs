namespace TwitchArchivist.Services;

public interface ITwitchApplicationConfigurationWriter
{
    Task UpdateTwitchClientCredentialsAsync(string clientId, string clientSecret, CancellationToken cancellationToken);
}
