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
        Jobs = (await dbContext.ArchiveJobs
            .Include(x => x.ChannelConfiguration)
            .ToListAsync())
            .OrderByDescending(x => x.CreatedUtc)
            .Take(50)
            .ToList();
    }
}
