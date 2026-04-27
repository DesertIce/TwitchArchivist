using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;
using TwitchArchivist.Services;

namespace TwitchArchivist.Pages;

public class IndexModel(TwitchArchivistDbContext dbContext, RuntimeStatusStore runtimeStatusStore) : PageModel
{
    public int ChannelCount { get; private set; }

    public int EnabledChannelCount { get; private set; }

    public int JobCount { get; private set; }

    public List<ArchiveJob> RecentJobs { get; private set; } = [];

    public RuntimeStatusStore RuntimeStatus => runtimeStatusStore;

    public string DownloaderStatusLabel => runtimeStatusStore.DownloaderExecutableValid ? "Configured" : "Attention needed";

    public async Task OnGetAsync()
    {
        ChannelCount = await dbContext.ChannelConfigurations.CountAsync();
        EnabledChannelCount = await dbContext.ChannelConfigurations.CountAsync(x => x.IsEnabled);
        JobCount = await dbContext.ArchiveJobs.CountAsync();
        RecentJobs = await dbContext.ArchiveJobs
            .Include(x => x.ChannelConfiguration)
            .OrderByDescending(x => x.CreatedUtc)
            .Take(10)
            .ToListAsync();
    }
}
