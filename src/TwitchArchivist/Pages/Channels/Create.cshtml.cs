using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Pages.Channels;

public class CreateModel(TwitchArchivistDbContext dbContext) : PageModel
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
            OutputDirectory = outputDirectory,
            IsEnabled = true,
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp
        });

        await dbContext.SaveChangesAsync();
        return RedirectToPage("/Channels/Index");
    }

    public class InputModel
    {
        [Display(Name = "Twitch login")]
        [Required]
        [StringLength(128)]
        public string TwitchLogin { get; set; } = string.Empty;

        [Display(Name = "Output directory")]
        [Required]
        [StringLength(1024)]
        public string OutputDirectory { get; set; } = string.Empty;
    }
}
