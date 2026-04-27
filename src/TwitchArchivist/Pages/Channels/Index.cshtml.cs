using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Pages.Channels;

public class IndexModel(TwitchArchivistDbContext dbContext) : PageModel
{
    public List<ChannelConfiguration> Channels { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Channels = await dbContext.ChannelConfigurations
            .OrderBy(x => x.TwitchLogin)
            .ToListAsync();
    }
}
