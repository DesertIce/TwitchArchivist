using Microsoft.Extensions.Logging;

namespace TwitchArchivist.Services.Logging;

public sealed record RecentLogEntry(
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string? Exception);
