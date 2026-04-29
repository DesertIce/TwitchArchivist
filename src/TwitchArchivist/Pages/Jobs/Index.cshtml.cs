using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Pages.Jobs;

public class IndexModel(TwitchArchivistDbContext dbContext) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Channel { get; set; }

    public List<ArchiveJob> Jobs { get; private set; } = [];

    public List<SelectListItem> StatusOptions { get; } = BuildStatusOptions();

    public List<SelectListItem> ChannelOptions { get; private set; } = [];

    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(Status) || !string.IsNullOrWhiteSpace(Channel);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ChannelOptions = await dbContext.ChannelConfigurations
            .OrderBy(x => x.TwitchLogin)
            .Select(x => new SelectListItem(x.TwitchLogin, x.TwitchLogin))
            .ToListAsync(cancellationToken);

        var query = dbContext.ArchiveJobs
            .Include(x => x.ChannelConfiguration)
            .OrderByDescending(x => x.Id)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(Status) &&
            Enum.TryParse<ArchiveJobStatus>(Status, ignoreCase: true, out var statusFilter))
        {
            Status = statusFilter.ToString();
            query = query.Where(x => x.Status == statusFilter);
        }
        else
        {
            Status = null;
        }

        if (!string.IsNullOrWhiteSpace(Channel))
        {
            var normalizedChannel = Channel.Trim();
            Channel = normalizedChannel;
            query = query.Where(x => x.ChannelConfiguration.TwitchLogin == normalizedChannel);
        }
        else
        {
            Channel = null;
        }

        Jobs = await query.Take(PageSize).ToListAsync(cancellationToken);
    }

    public static string FormatDuration(ArchiveJob job)
    {
        if (job.StartedUtc is not { } started)
        {
            return "—";
        }

        var endTime = job.CompletedUtc ?? DateTimeOffset.UtcNow;
        var duration = endTime - started;
        if (duration < TimeSpan.Zero)
        {
            return "—";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s";
        }

        return $"{(int)duration.TotalSeconds}s";
    }

    private static List<SelectListItem> BuildStatusOptions() =>
    [
        new SelectListItem("All statuses", string.Empty),
        ..Enum.GetValues<ArchiveJobStatus>()
            .Select(x => new SelectListItem(UiBadges.JobStatusLabel(x), x.ToString()))
    ];
}
