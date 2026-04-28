using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchDownloaderBinaryVerifierTests
{
    [Fact]
    public async Task VerifyAsync_AcceptsExpectedVersionBanner()
    {
        var executablePath = CreateExecutablePlaceholder();
        var verifier = new TwitchDownloaderBinaryVerifier(
            new FakeCommandLineRunner(new CommandLineResult(
                0,
                "TwitchDownloaderCLI 1.56.4+7e8b587c9c57e660bf53bbdd9bc11ad5d25dc1d8",
                string.Empty)));

        var result = await verifier.VerifyAsync(executablePath, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Contains("TwitchDownloaderCLI 1.56.4+", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_AcceptsExpectedVersionBannerWhenCommandExitsWithCodeOne()
    {
        var executablePath = CreateExecutablePlaceholder();
        var verifier = new TwitchDownloaderBinaryVerifier(
            new FakeCommandLineRunner(new CommandLineResult(
                1,
                string.Empty,
                """
                TwitchDownloaderCLI 1.56.4+7e8b587c9c57e660bf53bbdd9bc11ad5d25dc1d8

                Last updated 2026-04-28T16:46:11.0685025+00:00
                """)));

        var result = await verifier.VerifyAsync(executablePath, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Contains("TwitchDownloaderCLI 1.56.4+", result.Message, StringComparison.Ordinal);
        Assert.Contains("Last updated 2026-04-28T16:46:11.0685025+00:00", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnexpectedVersionOutput()
    {
        var executablePath = CreateExecutablePlaceholder();
        var verifier = new TwitchDownloaderBinaryVerifier(
            new FakeCommandLineRunner(new CommandLineResult(
                0,
                "SomeOtherTool 9.9.9",
                string.Empty)));

        var result = await verifier.VerifyAsync(executablePath, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("did not identify itself as TwitchDownloaderCLI", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RejectsFailedVersionCommand()
    {
        var executablePath = CreateExecutablePlaceholder();
        var verifier = new TwitchDownloaderBinaryVerifier(
            new FakeCommandLineRunner(new CommandLineResult(
                1,
                string.Empty,
                "boom")));

        var result = await verifier.VerifyAsync(executablePath, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("--version exited with code 1", result.Message, StringComparison.Ordinal);
    }

    private static string CreateExecutablePlaceholder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "placeholder");
        return path;
    }

    private sealed class FakeCommandLineRunner(CommandLineResult result) : ICommandLineRunner
    {
        public Task<CommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Assert.Single(arguments);
            Assert.Equal("--version", arguments[0]);
            return Task.FromResult(result);
        }
    }
}
