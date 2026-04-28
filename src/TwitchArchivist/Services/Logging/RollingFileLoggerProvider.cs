using Microsoft.Extensions.Logging;

namespace TwitchArchivist.Services.Logging;

public sealed class RollingFileLoggerProvider(RollingFileLogStore store, TimeProvider timeProvider) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(store, categoryName, timeProvider);

    public void Dispose()
    {
    }

    private sealed class RollingFileLogger(
        RollingFileLogStore store,
        string categoryName,
        TimeProvider timeProvider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            var timestamp = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'");
            var line = $"{timestamp} [{logLevel}] {categoryName}: {message}";
            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            store.Append(line);
        }
    }
}
