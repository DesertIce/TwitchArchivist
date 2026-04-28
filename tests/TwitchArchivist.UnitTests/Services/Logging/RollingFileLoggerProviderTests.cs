using Microsoft.Extensions.Logging;
using TwitchArchivist.Services.Logging;

namespace TwitchArchivist.UnitTests.Services.Logging;

public class RollingFileLoggerProviderTests
{
    [Fact]
    public void LoggerSuppressesEfCoreCommandInformationEntries()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 4, 28, 16, 45, 30, TimeSpan.Zero));
            var store = new RollingFileLogStore(root, "twitcharchivist", retainedDayCount: 3, clock);
            var provider = new RollingFileLoggerProvider(store, clock);
            var logger = provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

            logger.LogInformation("Executed DbCommand");
            logger.LogWarning("Slow DbCommand");

            var logFile = Path.Combine(root, "twitcharchivist-2026-04-28.log");
            var contents = File.ReadAllText(logFile);
            Assert.DoesNotContain("Executed DbCommand", contents, StringComparison.Ordinal);
            Assert.Contains("Slow DbCommand", contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("System.Net.Http.HttpClient.TwitchAccessTokenProvider.LogicalHandler", "request started", "token warning")]
    [InlineData("System.Net.Http.HttpClient.TwitchAccessTokenProvider.ClientHandler", "headers received", "token warning")]
    [InlineData("System.Net.Http.HttpClient.TwitchHelixClient.LogicalHandler", "request started", "helix warning")]
    [InlineData("System.Net.Http.HttpClient.TwitchHelixClient.ClientHandler", "headers received", "helix warning")]
    [InlineData("TwitchArchivist.Services.ConfigurationDiagnosticsHostedService", "Configuration diagnostics refreshed", "diagnostics warning")]
    public void LoggerSuppressesNoisyInformationEntries(string category, string informationMessage, string warningMessage)
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 4, 28, 16, 45, 30, TimeSpan.Zero));
            var store = new RollingFileLogStore(root, "twitcharchivist", retainedDayCount: 3, clock);
            var provider = new RollingFileLoggerProvider(store, clock);
            var logger = provider.CreateLogger(category);

            logger.LogInformation(informationMessage);
            logger.LogWarning(warningMessage);

            var logFile = Path.Combine(root, "twitcharchivist-2026-04-28.log");
            var contents = File.ReadAllText(logFile);
            Assert.DoesNotContain(informationMessage, contents, StringComparison.Ordinal);
            Assert.Contains(warningMessage, contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"twitcharchivist-logger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
