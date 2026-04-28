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
    IOptionsMonitor<DownloaderOptions> downloaderOptions,
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

    public void OnGet()
    {
        Input = new InputModel
        {
            DownloaderExecutablePath = downloaderOptions.CurrentValue.ExecutablePath ?? runtimeStatusStore.DownloaderExecutablePath ?? string.Empty
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

    public sealed class InputModel
    {
        [Display(Name = "TwitchDownloaderCLI executable")]
        [Required]
        [StringLength(1024)]
        public string DownloaderExecutablePath { get; set; } = string.Empty;
    }
}
