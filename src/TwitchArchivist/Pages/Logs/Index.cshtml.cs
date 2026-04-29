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

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public List<SelectListItem> LevelOptions { get; } = Enum.GetValues<LogLevel>()
        .Where(x => x != LogLevel.None)
        .Select(x => new SelectListItem(x.ToString(), x.ToString()))
        .ToList();

    public List<SelectListItem> CategoryOptions { get; private set; } = [];

    public IReadOnlyList<RecentLogEntry> Entries { get; private set; } = [];

    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(Category) || !string.IsNullOrWhiteSpace(Search);

    public void OnGet()
    {
        LoadEntries();
    }

    public IActionResult OnPost()
    {
        recentLogStore.Clear();
        return RedirectToPage(new
        {
            minimumLevel = NormalizeMinimumLevel().ToString(),
            category = string.IsNullOrWhiteSpace(Category) ? null : Category,
            search = string.IsNullOrWhiteSpace(Search) ? null : Search
        });
    }

    private void LoadEntries()
    {
        var minimumLevel = NormalizeMinimumLevel();
        var entries = recentLogStore.GetEntries(minimumLevel);

        CategoryOptions = entries
            .Select(x => x.Category)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new SelectListItem(x, x))
            .ToList();

        Category = string.IsNullOrWhiteSpace(Category) ? null : Category.Trim();
        Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

        IEnumerable<RecentLogEntry> filtered = entries;
        if (Category is not null)
        {
            filtered = filtered.Where(x => string.Equals(x.Category, Category, StringComparison.Ordinal));
        }

        if (Search is not null)
        {
            filtered = filtered.Where(x =>
                x.Message.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                (x.Exception is not null && x.Exception.Contains(Search, StringComparison.OrdinalIgnoreCase)));
        }

        Entries = filtered.ToArray();
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
