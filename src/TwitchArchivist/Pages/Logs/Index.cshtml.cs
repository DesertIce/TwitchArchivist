using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using TwitchArchivist.Services.Logging;

namespace TwitchArchivist.Pages.Logs;

[IgnoreAntiforgeryToken]
public class IndexModel(RecentLogStore recentLogStore) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string MinimumLevel { get; set; } = LogLevel.Information.ToString();

    public List<SelectListItem> LevelOptions { get; } = Enum.GetValues<LogLevel>()
        .Where(x => x != LogLevel.None)
        .Select(x => new SelectListItem(x.ToString(), x.ToString()))
        .ToList();

    public IReadOnlyList<RecentLogEntry> Entries { get; private set; } = [];

    public void OnGet()
    {
        LoadEntries();
    }

    public IActionResult OnPost()
    {
        recentLogStore.Clear();
        return RedirectToPage(new { minimumLevel = NormalizeMinimumLevel().ToString() });
    }

    private void LoadEntries()
    {
        Entries = recentLogStore.GetEntries(NormalizeMinimumLevel());
    }

    private LogLevel NormalizeMinimumLevel()
    {
        if (!Enum.TryParse<LogLevel>(MinimumLevel, ignoreCase: true, out var parsed) || parsed == LogLevel.None)
        {
            parsed = LogLevel.Information;
        }

        MinimumLevel = parsed.ToString();
        return parsed;
    }
}
