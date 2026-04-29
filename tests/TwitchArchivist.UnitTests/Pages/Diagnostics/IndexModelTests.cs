using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Pages.Diagnostics;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Pages.Diagnostics;

public class IndexModelTests
{
    [Fact]
    public async Task OnPostSaveDownloaderAsync_PersistsConfiguredPathAndRedirects()
    {
        var runtimeStatusStore = new RuntimeStatusStore();
        var writer = new FakeDownloaderConfigurationWriter();
        var model = new IndexModel(
            runtimeStatusStore,
            writer,
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchDownloaderBinaryVerifier())
        {
            Input = new IndexModel.InputModel
            {
                DownloaderExecutablePath = @"D:\Tools\TwitchDownloaderCLI.exe"
            }
        };

        var result = await model.OnPostSaveDownloaderAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Diagnostics/Index", redirect.PageName);
        Assert.Equal(@"D:\Tools\TwitchDownloaderCLI.exe", writer.SavedPath);
        Assert.Equal("saved", model.SaveStatus);
    }

    [Fact]
    public async Task OnPostSaveDownloaderAsync_RejectsBlankPath()
    {
        var runtimeStatusStore = new RuntimeStatusStore();
        var writer = new FakeDownloaderConfigurationWriter();
        var model = new IndexModel(
            runtimeStatusStore,
            writer,
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchDownloaderBinaryVerifier())
        {
            Input = new IndexModel.InputModel
            {
                DownloaderExecutablePath = "   "
            }
        };

        var result = await model.OnPostSaveDownloaderAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Null(writer.SavedPath);
    }

    [Fact]
    public void RuntimeStatusExposesConduitDiagnosticsFields()
    {
        var runtimeStatusStore = new RuntimeStatusStore();
        runtimeStatusStore.UpdateEventSubConduitStatus(
            "conduit-websocket",
            "conduit-123",
            4,
            3,
            1,
            "assign failed",
            "reconcile failed",
            new DateTimeOffset(2026, 4, 28, 23, 0, 0, TimeSpan.Zero));
        var model = new IndexModel(
            runtimeStatusStore,
            new FakeDownloaderConfigurationWriter(),
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchDownloaderBinaryVerifier());

        Assert.Equal("conduit-websocket", model.RuntimeStatus.EventSubTransportMode);
        Assert.Equal("conduit-123", model.RuntimeStatus.EventSubConduitId);
        Assert.Equal(4, model.RuntimeStatus.EventSubConfiguredShardCount);
        Assert.Equal(3, model.RuntimeStatus.EventSubActiveShardCount);
        Assert.Equal(1, model.RuntimeStatus.EventSubDisabledShardCount);
        Assert.Equal("assign failed", model.RuntimeStatus.EventSubLastShardAssignmentError);
        Assert.Equal("reconcile failed", model.RuntimeStatus.EventSubLastSubscriptionReconcileError);
        Assert.NotNull(model.RuntimeStatus.EventSubLastRateLimitUtc);
    }

    private sealed class FakeDownloaderConfigurationWriter : IDownloaderConfigurationWriter
    {
        public string? SavedPath { get; private set; }

        public Task UpdateDownloaderExecutablePathAsync(string executablePath, CancellationToken cancellationToken)
        {
            SavedPath = executablePath;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOptionsMonitor(DownloaderOptions currentValue) : IOptionsMonitor<DownloaderOptions>
    {
        public DownloaderOptions CurrentValue => currentValue;

        public DownloaderOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<DownloaderOptions, string?> listener) => null;
    }

    private sealed class FakeTwitchDownloaderBinaryVerifier : ITwitchDownloaderBinaryVerifier
    {
        public Task<TwitchDownloaderBinaryVerificationResult> VerifyAsync(string? executablePath, CancellationToken cancellationToken)
            => Task.FromResult(new TwitchDownloaderBinaryVerificationResult(true, "verified"));
    }
}
