using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;

namespace TwitchArchivist.Pages.Channels;

public class EditModel(TwitchArchivistDbContext dbContext) : PageModel
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
            OutputDirectory = entity.OutputDirectory,
            IsEnabled = entity.IsEnabled
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
        entity.OutputDirectory = Input.OutputDirectory.Trim();
        entity.IsEnabled = Input.IsEnabled;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync();
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

        [Display(Name = "Output directory")]
        [Required]
        [StringLength(1024)]
        public string OutputDirectory { get; set; } = string.Empty;

        public bool IsEnabled { get; set; }
    }
}
