using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public sealed class TwitchLiveStateSynchronizer(
    ITwitchHelixClient twitchHelixClient,
    IServiceScopeFactory scopeFactory,
    IArchiveJobTriggerService archiveJobTriggerService,
    TimeProvider timeProvider,
    ILogger<TwitchLiveStateSynchronizer> logger) : ITwitchLiveStateSynchronizer
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var channels = await dbContext.ChannelConfigurations
            .Include(x => x.StreamSessionState)
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.TwitchLogin)
            .ToListAsync(cancellationToken);

        if (channels.Count == 0)
        {
            return;
        }

        IReadOnlyList<TwitchLiveStreamState> liveStreams;
        try
        {
            liveStreams = await twitchHelixClient.GetLiveStreamsByLoginsAsync(
                channels.Select(x => x.TwitchLogin).ToArray(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to poll Helix for current live channel state");
            return;
        }

        var liveStreamByLogin = liveStreams.ToDictionary(
            x => x.BroadcasterLogin,
            StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();

        foreach (var channel in channels)
        {
            var state = channel.StreamSessionState;
            if (state is null && liveStreamByLogin.ContainsKey(channel.TwitchLogin))
            {
                state = new StreamSessionState
                {
                    ChannelConfigurationId = channel.Id,
                    CreatedUtc = now
                };
                dbContext.StreamSessionStates.Add(state);
                channel.StreamSessionState = state;
            }

            if (state is null)
            {
                continue;
            }

            if (liveStreamByLogin.TryGetValue(channel.TwitchLogin, out var liveStream))
            {
                channel.TwitchUserId = liveStream.BroadcasterUserId;
                channel.UpdatedUtc = now;
                state.LastKnownStreamId = liveStream.StreamId;
                state.LastOnlineUtc = liveStream.StartedAtUtc;
                state.LastOfflineUtc = null;
                state.UpdatedUtc = now;
                continue;
            }

            if (state.LastOnlineUtc.HasValue &&
                (!state.LastOfflineUtc.HasValue || state.LastOfflineUtc < state.LastOnlineUtc))
            {
                state.LastOfflineUtc = now;
                state.UpdatedUtc = now;
                channel.UpdatedUtc = now;

                try
                {
                    await archiveJobTriggerService.CreateArchiveJobFromOfflineAsync(
                        channel.TwitchLogin,
                        channel.TwitchUserId ?? string.Empty,
                        "live-state-poll",
                        now,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create archive job from live-state polling for channel {ChannelLogin}", channel.TwitchLogin);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
