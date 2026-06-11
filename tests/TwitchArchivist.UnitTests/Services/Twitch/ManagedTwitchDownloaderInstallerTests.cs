using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class ManagedTwitchDownloaderInstallerTests
{
    [Fact]
    public async Task InstallOrUpdateAsync_DownloadsWindowsCliReleaseExtractsItAndPersistsPath()
    {
        var contentRoot = Directory.CreateTempSubdirectory();
        try
        {
            var archiveBytes = CreateZipArchive(("TwitchDownloaderCLI.exe", "binary"));
            var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/lay295/TwitchDownloader/releases/latest")
                {
                    return JsonResponse(new
                    {
                        tag_name = "1.56.4",
                        assets = new[]
                        {
                            new
                            {
                                name = "TwitchDownloaderCLI-1.56.4-Windows-x64.zip",
                                browser_download_url = "https://example.test/TwitchDownloaderCLI-1.56.4-Windows-x64.zip"
                            }
                        }
                    });
                }

                if (request.RequestUri?.AbsoluteUri == "https://example.test/TwitchDownloaderCLI-1.56.4-Windows-x64.zip")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(archiveBytes)
                    };
                }

                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }));

            var writer = new FakeDownloaderConfigurationWriter();
            var installer = new ManagedTwitchDownloaderInstaller(
                httpClient,
                new FakeHostEnvironment(contentRoot.FullName),
                writer,
                new FakeOptionsMonitor(new DownloaderOptions { MaxConcurrentDownloads = 5 }));

            var result = await installer.InstallOrUpdateAsync(CancellationToken.None);

            Assert.Equal("1.56.4", result.Version);
            Assert.False(result.AlreadyInstalled);
            Assert.NotNull(writer.SavedPath);
            Assert.Equal(5, writer.SavedMaxConcurrentDownloads);
            Assert.EndsWith(@"tools\TwitchDownloaderCLI\current\TwitchDownloaderCLI.exe", writer.SavedPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(writer.SavedPath));
            var manifestPath = Path.Combine(contentRoot.FullName, "tools", "TwitchDownloaderCLI", "managed-install.json");
            Assert.True(File.Exists(manifestPath));
        }
        finally
        {
            contentRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InstallOrUpdateAsync_ReusesExistingManagedVersionWithoutRedownloading()
    {
        var contentRoot = Directory.CreateTempSubdirectory();
        try
        {
            var installDirectory = Path.Combine(contentRoot.FullName, "tools", "TwitchDownloaderCLI", "current");
            Directory.CreateDirectory(installDirectory);
            var executablePath = Path.Combine(installDirectory, "TwitchDownloaderCLI.exe");
            await File.WriteAllTextAsync(executablePath, "binary");
            var manifestPath = Path.Combine(contentRoot.FullName, "tools", "TwitchDownloaderCLI", "managed-install.json");
            await File.WriteAllTextAsync(
                manifestPath,
                """
                {
                  "version": "1.56.4",
                  "executablePath": "tools\\TwitchDownloaderCLI\\current\\TwitchDownloaderCLI.exe"
                }
                """);

            var releaseCalls = 0;
            var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/lay295/TwitchDownloader/releases/latest")
                {
                    releaseCalls++;
                    return JsonResponse(new
                    {
                        tag_name = "1.56.4",
                        assets = new[]
                        {
                            new
                            {
                                name = "TwitchDownloaderCLI-1.56.4-Windows-x64.zip",
                                browser_download_url = "https://example.test/TwitchDownloaderCLI-1.56.4-Windows-x64.zip"
                            }
                        }
                    });
                }

                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }));

            var writer = new FakeDownloaderConfigurationWriter();
            var installer = new ManagedTwitchDownloaderInstaller(
                httpClient,
                new FakeHostEnvironment(contentRoot.FullName),
                writer,
                new FakeOptionsMonitor(new DownloaderOptions { MaxConcurrentDownloads = 6 }));

            var result = await installer.InstallOrUpdateAsync(CancellationToken.None);

            Assert.Equal(1, releaseCalls);
            Assert.True(result.AlreadyInstalled);
            Assert.Equal(executablePath, writer.SavedPath);
            Assert.Equal(6, writer.SavedMaxConcurrentDownloads);
        }
        finally
        {
            contentRoot.Delete(recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(object payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
        };
    }

    private static byte[] CreateZipArchive(params (string Path, string Content)[] entries)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Path);
                using var stream = zipEntry.Open();
                using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(entry.Content);
            }
        }

        return memoryStream.ToArray();
    }

    private sealed class FakeDownloaderConfigurationWriter : IDownloaderConfigurationWriter
    {
        public string? SavedPath { get; private set; }
        public int? SavedMaxConcurrentDownloads { get; private set; }

        public Task UpdateDownloaderSettingsAsync(
            string executablePath,
            int maxConcurrentDownloads,
            CancellationToken cancellationToken)
        {
            SavedPath = executablePath;
            SavedMaxConcurrentDownloads = maxConcurrentDownloads;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOptionsMonitor(DownloaderOptions currentValue) : IOptionsMonitor<DownloaderOptions>
    {
        public DownloaderOptions CurrentValue => currentValue;

        public DownloaderOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<DownloaderOptions, string?> listener) => null;
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "TwitchArchivist.UnitTests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
