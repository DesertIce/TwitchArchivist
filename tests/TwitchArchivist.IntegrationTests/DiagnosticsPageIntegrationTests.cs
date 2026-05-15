using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace TwitchArchivist.IntegrationTests;

public class DiagnosticsPageIntegrationTests
{
    [Fact]
    public async Task DiagnosticsPageRendersDownloaderConfigFormAndFilePickerHooks()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/diagnostics");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Build context", payload);
        Assert.Contains("data-build-context=\"true\"", payload);
        Assert.Contains("TwitchArchivist", payload);
        Assert.Contains(".NETCoreApp,Version=v10.0", payload);
        Assert.Contains("Save TwitchDownloaderCLI path", payload);
        Assert.Contains("Set up managed TwitchDownloaderCLI", payload);
        Assert.Contains("Save Twitch application credentials", payload);
        Assert.Contains("Twitch developer console", payload);
        Assert.Contains("class=\"authorization-status-strip\"", payload);
        Assert.Contains("class=\"authorization-layout\"", payload);
        Assert.Contains("class=\"authorization-card\"", payload);
        Assert.Contains("class=\"callback-grid\"", payload);
        Assert.Contains("data-copy-button=\"true\"", payload);
        Assert.Contains("data-copy-source=\"twitch-callback-current\"", payload);
        Assert.Contains("data-copy-source=\"twitch-callback-http-dev\"", payload);
        Assert.Contains("data-copy-source=\"twitch-callback-https-dev\"", payload);
        Assert.Contains("data-file-picker=\"true\"", payload);
        Assert.Contains("data-file-picker-button=\"true\"", payload);
        Assert.Contains("data-file-picker-portal=\"true\"", payload);
    }

    [Fact]
    public async Task DiagnosticsPageRendersSavedTwitchCredentialsAndCallbackBlocks()
    {
        await using var factory = new IntegrationTestWebApplicationFactory(
            configureBuilder: builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                });
            },
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Twitch:ClientId"] = "client-id-123",
                ["Twitch:ClientSecret"] = "client-secret-456"
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/diagnostics");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("value=\"client-id-123\"", payload);
        Assert.Contains("value=\"client-secret-456\"", payload);
        Assert.Contains("readonly", payload);
        Assert.Contains("http://localhost/auth/twitch/callback", payload);
    }

    [Fact]
    public async Task DiagnosticsPageRendersDiagnosticsMessageAsStructuredLines()
    {
        await using var factory = new IntegrationTestWebApplicationFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/diagnostics");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<pre class=\"diagnostics-message mono\" id=\"runtime-diagnostics-message\">", payload);
    }
}
