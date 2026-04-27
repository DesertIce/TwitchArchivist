using System.Threading.Channels;

namespace TwitchArchivist.Services.Twitch;

public class ArchiveJobQueue : IArchiveJobQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    public ValueTask EnqueueAsync(int archiveJobId, CancellationToken cancellationToken)
    {
        return _channel.Writer.WriteAsync(archiveJobId, cancellationToken);
    }

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
