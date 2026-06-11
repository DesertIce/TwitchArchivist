using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Hosting;
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
    ITwitchDownloaderBinaryVerifier twitchDownloaderBinaryVerifier,
    IHostEnvironment hostEnvironment) : PageModel
{
    public const string SaveStatusDownloaderSaved = "saved";
    public const string SaveStatusDownloaderInstalled = "installed";
    public const string SaveStatusTwitchSaved = "twitch-saved";

    public RuntimeStatusStore RuntimeStatus => runtimeStatusStore;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [FromQuery]
    public string? SaveStatus { get; set; }

    public bool ShowDevCallbackUrls => hostEnvironment.IsDevelopment();

    public string TwitchAuthorizationBadgeClass => runtimeStatusStore.TwitchUserAuthorizationValidity switch
    {
        "valid" => "success",
        "expiring-soon" => "warn",
        _ => "danger"
    };

    public string TwitchOAuthCallbackUri => BuildTwitchOAuthRedirectUri();

    public void OnGet()
    {
        Input = new InputModel
        {
            DownloaderExecutablePath = downloaderOptions.CurrentValue.ExecutablePath ?? runtimeStatusStore.DownloaderExecutablePath ?? string.Empty,
            MaxConcurrentDownloads = Math.Max(1, downloaderOptions.CurrentValue.MaxConcurrentDownloads),
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

        if (Input.MaxConcurrentDownloads < 1)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.MaxConcurrentDownloads)}", "Max concurrent downloads must be at least 1.");
            return Page();
        }

        await downloaderConfigurationWriter.UpdateDownloaderSettingsAsync(
            normalizedPath,
            Input.MaxConcurrentDownloads,
            cancellationToken);

        var resolvedPath = DownloaderExecutablePathResolver.Resolve(normalizedPath);
        var verification = await twitchDownloaderBinaryVerifier.VerifyAsync(resolvedPath, cancellationToken);
        runtimeStatusStore.UpdateDownloaderValidation(resolvedPath, verification.IsValid, verification.Message);

        SaveStatus = SaveStatusDownloaderSaved;
        return RedirectToPage("/Diagnostics/Index", new { saveStatus = SaveStatus });
    }

    public async Task<IActionResult> OnPostInstallDownloaderAsync(CancellationToken cancellationToken)
    {
        var installResult = await managedTwitchDownloaderInstaller.InstallOrUpdateAsync(cancellationToken);
        var verification = await twitchDownloaderBinaryVerifier.VerifyAsync(installResult.ExecutablePath, cancellationToken);
        runtimeStatusStore.UpdateDownloaderValidation(installResult.ExecutablePath, verification.IsValid, verification.Message);

        SaveStatus = SaveStatusDownloaderInstalled;
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

        SaveStatus = SaveStatusTwitchSaved;
        return RedirectToPage("/Diagnostics/Index", new { saveStatus = SaveStatus });
    }

    public sealed class InputModel
    {
        [Display(Name = "TwitchDownloaderCLI executable")]
        [Required]
        [StringLength(1024)]
        public string DownloaderExecutablePath { get; set; } = string.Empty;

        [Display(Name = "Max concurrent downloads")]
        [Range(1, int.MaxValue, ErrorMessage = "Max concurrent downloads must be at least 1.")]
        public int MaxConcurrentDownloads { get; set; } = 2;

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
