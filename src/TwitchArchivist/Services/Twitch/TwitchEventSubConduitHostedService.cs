using TwitchArchivist.Models;

namespace TwitchArchivist.Services.Twitch;

public class TwitchEventSubConduitHostedService(
    IEventSubConduitCoordinator conduitCoordinator,
    EventSubConduitCleanupService cleanupService,
    Microsoft.Extensions.Options.IOptions<TwitchOptions> twitchOptions) : IHostedService
{
    private readonly TimeSpan _reconcileInterval = TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubConduitReconcileIntervalSeconds));
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _backgroundTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await conduitCoordinator.StartAsync(cancellationToken);
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = RunCleanupLoopAsync(_cancellationTokenSource.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancellationTokenSource is not null)
        {
            await _cancellationTokenSource.CancelAsync();
        }

        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await conduitCoordinator.StopAsync(cancellationToken);
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_reconcileInterval, cancellationToken);
                await cleanupService.RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
