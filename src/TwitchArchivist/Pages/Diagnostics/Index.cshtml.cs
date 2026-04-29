using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.Pages.Diagnostics;

public class IndexModel(
    RuntimeStatusStore runtimeStatusStore,
    IDownloaderConfigurationWriter downloaderConfigurationWriter,
    ITwitchApplicationConfigurationWriter twitchApplicationConfigurationWriter,
    IManagedTwitchDownloaderInstaller managedTwitchDownloaderInstaller,
    IOptionsMonitor<DownloaderOptions> downloaderOptions,
    IOptionsMonitor<TwitchOptions> twitchOptions,
    ITwitchDownloaderBinaryVerifier twitchDownloaderBinaryVerifier) : PageModel
{
    public RuntimeStatusStore RuntimeStatus => runtimeStatusStore;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [FromQuery]
    public string? SaveStatus { get; set; }

    public string TwitchAuthorizationBadgeClass => runtimeStatusStore.TwitchUserAuthorizationValidity switch
    {
        "valid" => string.Empty,
        "expiring-soon" => "warn",
        _ => "danger"
    };

    public string TwitchOAuthCallbackUri => BuildTwitchOAuthRedirectUri();

    public void OnGet()
    {
        Input = new InputModel
        {
            DownloaderExecutablePath = downloaderOptions.CurrentValue.ExecutablePath ?? runtimeStatusStore.DownloaderExecutablePath ?? string.Empty,
            TwitchClientId = twitchOptions.CurrentValue.ClientId ?? string.Empty,
            TwitchClientSecret = twitchOptions.CurrentValue.ClientSecret ?? string.Empty
        };
    }

    public async Task<IActionResult> OnPostSaveDownloaderAsync(CancellationToken cancellationToken)
    {
        var normalizedPath = Input.DownloaderExecutablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.DownloaderExecutablePath)}", "A TwitchDownloaderCLI executable path is required.");
            return Page();
        }

        await downloaderConfigurationWriter.UpdateDownloaderExecutablePathAsync(normalizedPath, cancellationToken);

        var resolvedPath = DownloaderExecutablePathResolver.Resolve(normalizedPath);
        var verification = await twitchDownloaderBinaryVerifier.VerifyAsync(resolvedPath, cancellationToken);
        runtimeStatusStore.UpdateDownloaderValidation(resolvedPath, verification.IsValid, verification.Message);

        SaveStatus = "saved";
        return RedirectToPage("/Diagnostics/Index", new { saveStatus = SaveStatus });
    }

    public async Task<IActionResult> OnPostInstallDownloaderAsync(CancellationToken cancellationToken)
    {
        var installResult = await managedTwitchDownloaderInstaller.InstallOrUpdateAsync(cancellationToken);
        var verification = await twitchDownloaderBinaryVerifier.VerifyAsync(installResult.ExecutablePath, cancellationToken);
        runtimeStatusStore.UpdateDownloaderValidation(installResult.ExecutablePath, verification.IsValid, verification.Message);

        SaveStatus = "installed";
        return RedirectToPage("/Diagnostics/Index", new { saveStatus = SaveStatus });
    }

    public async Task<IActionResult> OnPostSaveTwitchAppAsync(CancellationToken cancellationToken)
    {
        var normalizedClientId = Input.TwitchClientId?.Trim() ?? string.Empty;
        var normalizedClientSecret = Input.TwitchClientSecret?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedClientId))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.TwitchClientId)}", "A Twitch client id is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedClientSecret))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.TwitchClientSecret)}", "A Twitch client secret is required.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        await twitchApplicationConfigurationWriter.UpdateTwitchClientCredentialsAsync(
            normalizedClientId,
            normalizedClientSecret,
            cancellationToken);

        SaveStatus = "twitch-saved";
        return RedirectToPage("/Diagnostics/Index", new { saveStatus = SaveStatus });
    }

    public sealed class InputModel
    {
        [Display(Name = "TwitchDownloaderCLI executable")]
        [Required]
        [StringLength(1024)]
        public string DownloaderExecutablePath { get; set; } = string.Empty;

        [Display(Name = "Twitch client id")]
        [StringLength(256)]
        public string TwitchClientId { get; set; } = string.Empty;

        [Display(Name = "Twitch client secret")]
        [StringLength(512)]
        public string TwitchClientSecret { get; set; } = string.Empty;
    }

    private string BuildTwitchOAuthRedirectUri()
    {
        var host = Request.Host.Host;
        if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = "localhost";
        }

        var port = Request.Host.Port.HasValue ? $":{Request.Host.Port.Value}" : string.Empty;
        return $"{Request.Scheme}://{host}{port}/auth/twitch/callback";
    }
}
