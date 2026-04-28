using Microsoft.Extensions.Logging;

namespace TwitchArchivist.Services.Logging;

internal static class LogCategoryFilter
{
    private const string EfCoreDatabaseCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    public static bool ShouldLog(string categoryName, LogLevel logLevel)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        if (string.Equals(categoryName, EfCoreDatabaseCommandCategory, StringComparison.Ordinal) &&
            logLevel < LogLevel.Warning)
        {
            return false;
        }

        return true;
    }
}
