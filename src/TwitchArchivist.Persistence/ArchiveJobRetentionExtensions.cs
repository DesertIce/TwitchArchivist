using Microsoft.EntityFrameworkCore;

namespace TwitchArchivist.Persistence;

public static class ArchiveJobRetentionExtensions
{
    public static async Task TrimArchiveJobsForChannelAsync(
        this TwitchArchivistDbContext dbContext,
        int channelConfigurationId,
        int maximumJobsToKeep,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumJobsToKeep);

        var jobIdsToDelete = (await dbContext.ArchiveJobs
            .Where(x => x.ChannelConfigurationId == channelConfigurationId)
            .Select(x => new { x.Id, x.CreatedUtc })
            .ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedUtc)
            .ThenByDescending(x => x.Id)
            .Skip(maximumJobsToKeep)
            .Select(x => x.Id)
            .ToList();

        if (jobIdsToDelete.Count == 0)
        {
            return;
        }

        await dbContext.ArchiveJobs
            .Where(x => jobIdsToDelete.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
