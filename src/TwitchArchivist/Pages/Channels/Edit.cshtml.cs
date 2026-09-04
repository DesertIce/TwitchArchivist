using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.Pages.Channels;

public class EditModel(
    TwitchArchivistDbContext dbContext,
    ITwitchLiveStateSynchronizer twitchLiveStateSynchronizer) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var entity = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.Id == id);
        if (entity is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            Id = entity.Id,
            TwitchLogin = entity.TwitchLogin,
            Alias = entity.Alias,
            OutputDirectory = entity.OutputDirectory,
            IsEnabled = entity.IsEnabled,
            AutoPruneEnabled = entity.AutoPruneEnabled,
            AutoPruneVodCount = entity.AutoPruneVodCount,
            CompressEnabled = entity.CompressEnabled,
            CompressVodCount = entity.CompressVodCount
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var entity = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.Id == Input.Id);
        if (entity is null)
        {
            return NotFound();
        }

        var normalizedLogin = Input.TwitchLogin.Trim().ToLowerInvariant();
        if (await dbContext.ChannelConfigurations.AnyAsync(x => x.Id != Input.Id && x.TwitchLogin == normalizedLogin))
        {
            ModelState.AddModelError(string.Empty, "That Twitch login is already configured.");
            return Page();
        }

        Directory.CreateDirectory(Input.OutputDirectory.Trim());

        entity.TwitchLogin = normalizedLogin;
        entity.Alias = string.IsNullOrWhiteSpace(Input.Alias) ? null : Input.Alias.Trim();
        entity.OutputDirectory = Input.OutputDirectory.Trim();
        entity.IsEnabled = Input.IsEnabled;
        entity.AutoPruneEnabled = Input.AutoPruneEnabled;
        entity.AutoPruneVodCount = Input.AutoPruneVodCount;
        entity.CompressEnabled = Input.CompressEnabled;
        entity.CompressVodCount = Input.CompressVodCount;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync();
        await twitchLiveStateSynchronizer.SynchronizeAsync(HttpContext?.RequestAborted ?? CancellationToken.None);
        return RedirectToPage("/Channels/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var entity = await dbContext.ChannelConfigurations.SingleOrDefaultAsync(x => x.Id == Input.Id);
        if (entity is null)
        {
            return NotFound();
        }

        dbContext.ChannelConfigurations.Remove(entity);
        await dbContext.SaveChangesAsync();
        return RedirectToPage("/Channels/Index");
    }

    public class InputModel
    {
        public int Id { get; set; }

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

        public bool IsEnabled { get; set; }

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
