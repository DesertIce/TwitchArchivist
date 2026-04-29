namespace TwitchArchivist.Services.Twitch;

public interface IEventSubConduitCoordinator
{
    Task StartAsync(CancellationToken cancellationToken);

    Task ReconcileAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
