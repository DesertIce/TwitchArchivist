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

    public string TwitchAuthorizationStatusLabel => runtimeStatusStore.TwitchUserAuthorizationValidity;

    public string TwitchAuthorizationBadgeClass => runtimeStatusStore.TwitchUserAuthorizationValidity switch
    {
        "valid" => string.Empty,
        "expiring-soon" => "warn",
        _ => "danger"
    };

    public string TwitchAuthorizationDetail => runtimeStatusStore.TwitchUserAuthorizationConfigured
        ? runtimeStatusStore.TwitchUserAuthorizationDetail ?? $"Authorized as {runtimeStatusStore.TwitchUserAuthorizationLogin ?? "unknown"}"
        : "Authorize a Twitch user token so EventSub WebSocket subscriptions can be created.";

    public async Task OnGetAsync()
    {
        ChannelCount = await dbContext.ChannelConfigurations.CountAsync();
        EnabledChannelCount = await dbContext.ChannelConfigurations.CountAsync(x => x.IsEnabled);
        JobCount = await dbContext.ArchiveJobs.CountAsync();
        RecentJobs = await dbContext.ArchiveJobs
            .Include(x => x.ChannelConfiguration)
            .OrderByDescending(x => x.Id)
            .Take(10)
            .ToListAsync();
    }
}
