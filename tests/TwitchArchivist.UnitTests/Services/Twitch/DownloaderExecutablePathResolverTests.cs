using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class DownloaderExecutablePathResolverTests
{
    [Fact]
    public void ResolvePrefersConfiguredPathWhenProvided()
    {
        var resolvedPath = DownloaderExecutablePathResolver.Resolve(
            @"D:\Tools\TwitchDownloaderCLI.exe",
            @"C:\Users\Example\AppData\Roaming");

        Assert.Equal(@"D:\Tools\TwitchDownloaderCLI.exe", resolvedPath);
    }

    [Fact]
    public void ResolveFallsBackToAppDataWhenConfiguredPathMissing()
    {
        var resolvedPath = DownloaderExecutablePathResolver.Resolve(
            null,
            @"C:\Users\Example\AppData\Roaming");

        Assert.Equal(
            @"C:\Users\Example\AppData\Roaming\TwitchDownloaderCLI\TwitchDownloaderCLI.exe",
            resolvedPath);
    }
}
