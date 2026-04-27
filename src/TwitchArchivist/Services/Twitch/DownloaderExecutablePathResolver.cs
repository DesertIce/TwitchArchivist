namespace TwitchArchivist.Services.Twitch;

public static class DownloaderExecutablePathResolver
{
    public static string? Resolve(string? configuredPath, string? appDataPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var resolvedAppData = appDataPath;
        if (string.IsNullOrWhiteSpace(resolvedAppData))
        {
            resolvedAppData = Environment.GetEnvironmentVariable("APPDATA");
        }

        if (string.IsNullOrWhiteSpace(resolvedAppData))
        {
            return null;
        }

        return Path.Combine(resolvedAppData, "TwitchDownloaderCLI", "TwitchDownloaderCLI.exe");
    }
}
