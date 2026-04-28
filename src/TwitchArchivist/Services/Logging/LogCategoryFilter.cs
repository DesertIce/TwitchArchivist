using Microsoft.Extensions.Logging;

namespace TwitchArchivist.Services.Logging;

internal static class LogCategoryFilter
{
    private static readonly HashSet<string> SuppressedInformationCategories = new(StringComparer.Ordinal)
    {
        "Microsoft.EntityFrameworkCore.Database.Command",
        "System.Net.Http.HttpClient.TwitchAccessTokenProvider.LogicalHandler",
        "System.Net.Http.HttpClient.TwitchAccessTokenProvider.ClientHandler",
        "System.Net.Http.HttpClient.TwitchHelixClient.LogicalHandler",
        "System.Net.Http.HttpClient.TwitchHelixClient.ClientHandler",
        "TwitchArchivist.Services.ConfigurationDiagnosticsHostedService"
    };

    public static bool ShouldLog(string categoryName, LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        if (SuppressedInformationCategories.Contains(categoryName) &&
            logLevel < LogLevel.Warning)
        {
            return false;
        }

        return true;
    }
}
