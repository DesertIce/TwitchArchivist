using System.Net;

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
        Assert.Contains("Save TwitchDownloaderCLI path", payload);
        Assert.Contains("data-file-picker=\"true\"", payload);
        Assert.Contains("data-file-picker-button=\"true\"", payload);
        Assert.Contains("data-file-picker-portal=\"true\"", payload);
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
