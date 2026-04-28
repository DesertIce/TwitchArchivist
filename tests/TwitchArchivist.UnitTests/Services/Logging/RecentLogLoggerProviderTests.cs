using Microsoft.Extensions.Logging;
using TwitchArchivist.Services.Logging;

namespace TwitchArchivist.UnitTests.Services.Logging;

public class RecentLogLoggerProviderTests
{
    [Fact]
    public void LoggerSuppressesEfCoreCommandInformationEntries()
    {
        var store = new RecentLogStore(capacity: 10);
        var provider = new RecentLogLoggerProvider(store);
        var logger = provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogInformation("Executed DbCommand");
        logger.LogWarning("Slow DbCommand");

        var entries = store.GetEntries(LogLevel.Trace);
        Assert.Single(entries);
        Assert.Equal("Slow DbCommand", entries[0].Message);
    }

    [Theory]
    [InlineData("System.Net.Http.HttpClient.TwitchAccessTokenProvider.LogicalHandler", "request started", "token warning")]
    [InlineData("System.Net.Http.HttpClient.TwitchAccessTokenProvider.ClientHandler", "headers received", "token warning")]
    [InlineData("System.Net.Http.HttpClient.TwitchHelixClient.LogicalHandler", "request started", "helix warning")]
    [InlineData("System.Net.Http.HttpClient.TwitchHelixClient.ClientHandler", "headers received", "helix warning")]
    [InlineData("TwitchArchivist.Services.ConfigurationDiagnosticsHostedService", "Configuration diagnostics refreshed", "diagnostics warning")]
    public void LoggerSuppressesNoisyInformationEntries(string category, string informationMessage, string warningMessage)
    {
        var store = new RecentLogStore(capacity: 10);
        var provider = new RecentLogLoggerProvider(store);
        var logger = provider.CreateLogger(category);

        logger.LogInformation(informationMessage);
        logger.LogWarning(warningMessage);

        var entries = store.GetEntries(LogLevel.Trace);
        Assert.Single(entries);
        Assert.Equal(warningMessage, entries[0].Message);
    }
}
