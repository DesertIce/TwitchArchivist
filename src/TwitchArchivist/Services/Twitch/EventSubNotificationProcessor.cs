using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public class EventSubNotificationProcessor(
    IServiceScopeFactory scopeFactory,
    IArchiveJobTriggerService archiveJobTriggerService,
    ILogger<EventSubNotificationProcessor> logger,
    TimeProvider timeProvider)
{
    public async Task HandleStreamOnlineAsync(object? sender, EventSubStreamOnlineEventArgs args)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var login = args.BroadcasterUserLogin.ToLowerInvariant();
        var channel = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.TwitchLogin == login);
        if (channel is null)
        {
            return;
        }

        channel.TwitchUserId = args.BroadcasterUserId;
        channel.UpdatedUtc = timeProvider.GetUtcNow();

        var state = await dbContext.StreamSessionStates.SingleOrDefaultAsync(x => x.ChannelConfigurationId == channel.Id);
        if (state is null)
        {
            state = new StreamSessionState
            {
                ChannelConfigurationId = channel.Id,
                CreatedUtc = timeProvider.GetUtcNow()
            };
            dbContext.StreamSessionStates.Add(state);
        }

        state.LastKnownStreamId = args.StreamId;
        state.LastOnlineUtc = args.StartedAtUtc;
        state.UpdatedUtc = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync();
    }

    public async Task HandleStreamOfflineAsync(object? sender, EventSubStreamOfflineEventArgs args)
    {
        var login = args.BroadcasterUserLogin.ToLowerInvariant();
        logger.LogInformation("Received EventSub stream.offline notification for channel {ChannelLogin}", login);

        try
        {
            await archiveJobTriggerService.CreateArchiveJobFromOfflineAsync(
                login,
                args.BroadcasterUserId,
                "stream.offline",
                timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Failed to create archive job from stream.offline for channel {ChannelLogin} and broadcaster user id {BroadcasterUserId}",
                login,
                args.BroadcasterUserId);
        }
    }
}
