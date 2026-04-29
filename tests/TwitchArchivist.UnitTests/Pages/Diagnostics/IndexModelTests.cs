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
            writer,
            new FakeManagedTwitchDownloaderInstaller(),
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchOptionsMonitor(new TwitchOptions()),
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
            writer,
            new FakeManagedTwitchDownloaderInstaller(),
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchOptionsMonitor(new TwitchOptions()),
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
            new FakeDownloaderConfigurationWriter(),
            new FakeManagedTwitchDownloaderInstaller(),
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchOptionsMonitor(new TwitchOptions()),
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

    [Fact]
    public async Task OnPostInstallDownloaderAsync_InstallsManagedDownloaderAndRedirects()
    {
        var runtimeStatusStore = new RuntimeStatusStore();
        var installer = new FakeManagedTwitchDownloaderInstaller
        {
            Result = new ManagedTwitchDownloaderInstallResult(
                @"D:\apps\TwitchArchivist\tools\TwitchDownloaderCLI\current\TwitchDownloaderCLI.exe",
                "1.56.4",
                false)
        };
        var model = new IndexModel(
            runtimeStatusStore,
            new FakeDownloaderConfigurationWriter(),
            new FakeDownloaderConfigurationWriter(),
            installer,
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchOptionsMonitor(new TwitchOptions()),
            new FakeTwitchDownloaderBinaryVerifier());

        var result = await model.OnPostInstallDownloaderAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Diagnostics/Index", redirect.PageName);
        Assert.True(installer.WasCalled);
        Assert.Equal("installed", model.SaveStatus);
    }

    [Fact]
    public async Task OnPostSaveTwitchAppAsync_PersistsCredentialsAndRedirects()
    {
        var runtimeStatusStore = new RuntimeStatusStore();
        var writer = new FakeDownloaderConfigurationWriter();
        var model = new IndexModel(
            runtimeStatusStore,
            writer,
            writer,
            new FakeManagedTwitchDownloaderInstaller(),
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchOptionsMonitor(new TwitchOptions()),
            new FakeTwitchDownloaderBinaryVerifier())
        {
            Input = new IndexModel.InputModel
            {
                TwitchClientId = "client-id-123",
                TwitchClientSecret = "client-secret-456"
            }
        };

        var result = await model.OnPostSaveTwitchAppAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Diagnostics/Index", redirect.PageName);
        Assert.Equal("client-id-123", writer.SavedClientId);
        Assert.Equal("client-secret-456", writer.SavedClientSecret);
        Assert.Equal("twitch-saved", model.SaveStatus);
    }

    [Fact]
    public async Task OnPostSaveTwitchAppAsync_RejectsBlankValues()
    {
        var runtimeStatusStore = new RuntimeStatusStore();
        var writer = new FakeDownloaderConfigurationWriter();
        var model = new IndexModel(
            runtimeStatusStore,
            writer,
            writer,
            new FakeManagedTwitchDownloaderInstaller(),
            new FakeOptionsMonitor(new DownloaderOptions()),
            new FakeTwitchOptionsMonitor(new TwitchOptions()),
            new FakeTwitchDownloaderBinaryVerifier())
        {
            Input = new IndexModel.InputModel
            {
                TwitchClientId = " ",
                TwitchClientSecret = " "
            }
        };

        var result = await model.OnPostSaveTwitchAppAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Null(writer.SavedClientId);
        Assert.Null(writer.SavedClientSecret);
    }

    private sealed class FakeDownloaderConfigurationWriter : IDownloaderConfigurationWriter, ITwitchApplicationConfigurationWriter
    {
        public string? SavedPath { get; private set; }
        public string? SavedClientId { get; private set; }
        public string? SavedClientSecret { get; private set; }

        public Task UpdateDownloaderExecutablePathAsync(string executablePath, CancellationToken cancellationToken)
        {
            SavedPath = executablePath;
            return Task.CompletedTask;
        }

        public Task UpdateTwitchClientCredentialsAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
        {
            SavedClientId = clientId;
            SavedClientSecret = clientSecret;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOptionsMonitor(DownloaderOptions currentValue) : IOptionsMonitor<DownloaderOptions>
    {
        public DownloaderOptions CurrentValue => currentValue;

        public DownloaderOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<DownloaderOptions, string?> listener) => null;
    }

    private sealed class FakeTwitchOptionsMonitor(TwitchOptions currentValue) : IOptionsMonitor<TwitchOptions>
    {
        public TwitchOptions CurrentValue => currentValue;

        public TwitchOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<TwitchOptions, string?> listener) => null;
    }

    private sealed class FakeTwitchDownloaderBinaryVerifier : ITwitchDownloaderBinaryVerifier
    {
        public Task<TwitchDownloaderBinaryVerificationResult> VerifyAsync(string? executablePath, CancellationToken cancellationToken)
            => Task.FromResult(new TwitchDownloaderBinaryVerificationResult(true, "verified"));
    }

    private sealed class FakeManagedTwitchDownloaderInstaller : IManagedTwitchDownloaderInstaller
    {
        public ManagedTwitchDownloaderInstallResult Result { get; set; } =
            new(@"D:\managed\TwitchDownloaderCLI.exe", "1.56.4", false);

        public bool WasCalled { get; private set; }

        public Task<ManagedTwitchDownloaderInstallResult> InstallOrUpdateAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(Result);
        }
    }
}
