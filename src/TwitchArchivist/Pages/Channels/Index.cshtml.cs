using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;

namespace TwitchArchivist.Pages.Channels;

public class IndexModel(TwitchArchivistDbContext dbContext) : PageModel
{
    public List<ChannelRow> Channels { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Channels = await dbContext.ChannelConfigurations
            .Include(x => x.StreamSessionState)
            .OrderBy(x => x.TwitchLogin)
            .Select(x => new ChannelRow(
                x.Id,
                x.TwitchLogin,
                x.OutputDirectory,
                x.IsEnabled,
                x.UpdatedUtc,
                x.StreamSessionState != null &&
                x.StreamSessionState.LastOnlineUtc.HasValue &&
                (!x.StreamSessionState.LastOfflineUtc.HasValue ||
                 x.StreamSessionState.LastOnlineUtc > x.StreamSessionState.LastOfflineUtc),
                x.StreamSessionState != null ? x.StreamSessionState.LastOnlineUtc : null))
            .ToListAsync();
    }

    public sealed record ChannelRow(
        int Id,
        string TwitchLogin,
        string OutputDirectory,
        bool IsEnabled,
        DateTimeOffset UpdatedUtc,
        bool IsLive,
        DateTimeOffset? LastLiveUtc);
}
