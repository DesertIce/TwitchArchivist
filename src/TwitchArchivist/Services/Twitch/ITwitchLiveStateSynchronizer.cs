namespace TwitchArchivist.Services.Twitch;

public interface ITwitchLiveStateSynchronizer
{
    Task SynchronizeAsync(CancellationToken cancellationToken);
}
