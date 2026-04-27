namespace TwitchArchivist.Services.Twitch;

public interface IArchiveJobQueue
{
    ValueTask EnqueueAsync(int archiveJobId, CancellationToken cancellationToken);

    IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken);
}
