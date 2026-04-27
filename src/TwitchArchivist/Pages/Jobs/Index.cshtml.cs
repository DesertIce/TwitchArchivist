using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Pages.Jobs;

public class IndexModel(TwitchArchivistDbContext dbContext) : PageModel
{
    public List<ArchiveJob> Jobs { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Jobs = await dbContext.ArchiveJobs
            .Include(x => x.ChannelConfiguration)
            .OrderByDescending(x => x.Id)
            .Take(50)
            .ToListAsync();
    }
}
