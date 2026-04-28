namespace TwitchArchivist.Services.Twitch;

public interface IEventSubSubscriptionSynchronizer
{
    Task EnsureSubscriptionsAsync(string sessionId, CancellationToken cancellationToken);
}
