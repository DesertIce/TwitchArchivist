using Microsoft.Extensions.Logging;

namespace TwitchArchivist.Services.Logging;

public class RecentLogStore(int capacity)
{
    private readonly object _sync = new();
    private readonly Queue<RecentLogEntry> _entries = new();
    private readonly int _capacity = Math.Max(1, capacity);

    public void Append(LogLevel level, string message, string category, EventId eventId = default, Exception? exception = null)
    {
        var entry = new RecentLogEntry(
            DateTimeOffset.UtcNow,
            level,
            category,
            eventId,
            message,
            exception?.ToString());

        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    public IReadOnlyList<RecentLogEntry> GetEntries(LogLevel minimumLevel)
    {
        lock (_sync)
        {
            return _entries
                .Where(x => x.Level >= minimumLevel)
                .Reverse()
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
