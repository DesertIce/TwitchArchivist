using Microsoft.Extensions.Logging;

namespace TwitchArchivist.Services.Logging;

public sealed class RecentLogLoggerProvider(RecentLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RecentLogLogger(store, categoryName);

    public void Dispose()
    {
    }

    private sealed class RecentLogLogger(RecentLogStore store, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => LogCategoryFilter.ShouldLog(categoryName, logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            store.Append(logLevel, message, categoryName, eventId, exception);
        }
    }
}
