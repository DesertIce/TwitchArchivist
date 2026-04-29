using System.Text.Json;
using TwitchArchivist.Services;

namespace TwitchArchivist.UnitTests.Services;

public class AppSettingsDownloaderConfigurationWriterTests
{
    [Fact]
    public async Task UpdateDownloaderExecutablePathAsync_WritesDownloaderPathAndPreservesOtherSettings()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(tempDirectory.FullName, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath,
                """
                {
                  "Storage": {
                    "DatabasePath": "data/twitcharchivist.db"
                  },
                  "Downloader": {
                    "ExecutablePath": ""
                  },
                  "AllowedHosts": "*"
                }
                """);

            var writer = new AppSettingsDownloaderConfigurationWriter(appSettingsPath);

            await writer.UpdateDownloaderExecutablePathAsync(
                @"D:\Tools\TwitchDownloaderCLI.exe",
                CancellationToken.None);

            await using var stream = File.OpenRead(appSettingsPath);
            using var document = await JsonDocument.ParseAsync(stream);

            Assert.Equal(
                @"D:\Tools\TwitchDownloaderCLI.exe",
                document.RootElement.GetProperty("Downloader").GetProperty("ExecutablePath").GetString());
            Assert.Equal(
                "data/twitcharchivist.db",
                document.RootElement.GetProperty("Storage").GetProperty("DatabasePath").GetString());
            Assert.Equal("*", document.RootElement.GetProperty("AllowedHosts").GetString());
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UpdateTwitchClientCredentialsAsync_WritesCredentialsAndPreservesOtherSettings()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(tempDirectory.FullName, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath,
                """
                {
                  "Storage": {
                    "DatabasePath": "data/twitcharchivist.db"
                  },
                  "Twitch": {
                    "ClientId": "",
                    "ClientSecret": ""
                  },
                  "AllowedHosts": "*"
                }
                """);

            var writer = new AppSettingsDownloaderConfigurationWriter(appSettingsPath);

            await writer.UpdateTwitchClientCredentialsAsync(
                "client-id-123",
                "client-secret-456",
                CancellationToken.None);

            await using var stream = File.OpenRead(appSettingsPath);
            using var document = await JsonDocument.ParseAsync(stream);

            Assert.Equal(
                "client-id-123",
                document.RootElement.GetProperty("Twitch").GetProperty("ClientId").GetString());
            Assert.Equal(
                "client-secret-456",
                document.RootElement.GetProperty("Twitch").GetProperty("ClientSecret").GetString());
            Assert.Equal(
                "data/twitcharchivist.db",
                document.RootElement.GetProperty("Storage").GetProperty("DatabasePath").GetString());
            Assert.Equal("*", document.RootElement.GetProperty("AllowedHosts").GetString());
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }
}
