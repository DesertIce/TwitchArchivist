using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.Pages.Channels;

public class CreateModel(
    TwitchArchivistDbContext dbContext,
    ITwitchLiveStateSynchronizer twitchLiveStateSynchronizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var normalizedLogin = Input.TwitchLogin.Trim().ToLowerInvariant();
        var outputDirectory = Input.OutputDirectory.Trim();

        if (await dbContext.ChannelConfigurations.AnyAsync(x => x.TwitchLogin == normalizedLogin))
        {
            ModelState.AddModelError(string.Empty, "That Twitch login is already configured.");
            return Page();
        }

        Directory.CreateDirectory(outputDirectory);

        var timestamp = DateTimeOffset.UtcNow;
        dbContext.ChannelConfigurations.Add(new ChannelConfiguration
        {
            TwitchLogin = normalizedLogin,
            Alias = string.IsNullOrWhiteSpace(Input.Alias) ? null : Input.Alias.Trim(),
            OutputDirectory = outputDirectory,
            IsEnabled = true,
            AutoPruneEnabled = Input.AutoPruneEnabled,
            AutoPruneVodCount = Input.AutoPruneVodCount,
            CompressEnabled = Input.CompressEnabled,
            CompressVodCount = Input.CompressVodCount,
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp
        });

        await dbContext.SaveChangesAsync();
        await twitchLiveStateSynchronizer.SynchronizeAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
        return RedirectToPage("/Channels/Index");
    }

    public class InputModel
    {
        [Display(Name = "Twitch login")]
        [Required]
        [StringLength(128)]
        public string TwitchLogin { get; set; } = string.Empty;

        [Display(Name = "Filename alias")]
        [StringLength(128)]
        public string? Alias { get; set; }

        [Display(Name = "Output directory")]
        [Required]
        [StringLength(1024)]
        public string OutputDirectory { get; set; } = string.Empty;

        [Display(Name = "Auto prune older VOD files")]
        public bool AutoPruneEnabled { get; set; }

        [Display(Name = "Keep most recent VOD count")]
        [Range(1, 1000)]
        public int AutoPruneVodCount { get; set; } = 10;

        [Display(Name = "Compress older VOD files")]
        public bool CompressEnabled { get; set; }

        [Display(Name = "Additional compressed VOD count")]
        [Range(1, 1000)]
        public int CompressVodCount { get; set; } = 10;
    }
}
