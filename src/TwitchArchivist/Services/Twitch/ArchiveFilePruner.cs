using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public static class ArchiveFilePruner
{
    public static async Task PruneSucceededFilesForChannelAsync(
        TwitchArchivistDbContext dbContext,
        ChannelConfiguration channel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!channel.AutoPruneEnabled)
        {
            return;
        }

        if (channel.AutoPruneVodCount <= 0 || string.IsNullOrWhiteSpace(channel.OutputDirectory))
        {
            return;
        }

        var outputDirectory = Path.GetFullPath(channel.OutputDirectory);
        var channelFilenamePrefix = ArchiveOutputPathBuilder.GetChannelFilenamePrefix(channel.TwitchLogin);
        var successfulJobs = await dbContext.ArchiveJobs
            .Where(x => x.ChannelConfigurationId == channel.Id &&
                        x.Status == ArchiveJobStatus.Succeeded &&
                        x.OutputPath != null)
            .Select(x => new SuccessfulArchiveFileRecord(x.Id, x.OutputPath!, x.CompletedUtc, x.CreatedUtc))
            .ToListAsync(cancellationToken);

        var existingFiles = successfulJobs
            .OrderByDescending(x => x.CompletedUtc ?? x.CreatedUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id,
                FullPath = NormalizeIfUnderRootAndMatchChannelPrefix(x.OutputPath, outputDirectory, channelFilenamePrefix)
            })
            .Where(x => x.FullPath is not null && File.Exists(x.FullPath))
            .ToList();

        foreach (var file in existingFiles.Skip(channel.AutoPruneVodCount))
        {
            try
            {
                File.Delete(file.FullPath!);
                logger.LogInformation(
                    "Auto pruned archived VOD file for channel {ChannelLogin}: {OutputPath}",
                    channel.TwitchLogin,
                    file.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to auto prune archived VOD file for channel {ChannelLogin}: {OutputPath}",
                    channel.TwitchLogin,
                    file.FullPath);
            }
        }
    }

    private static string? NormalizeIfUnderRootAndMatchChannelPrefix(string outputPath, string rootDirectory, string channelFilenamePrefix)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var normalizedRoot = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var filename = Path.GetFileName(fullPath);
        if (!string.IsNullOrWhiteSpace(channelFilenamePrefix) &&
            !filename.StartsWith(channelFilenamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fullPath;
    }

    private sealed record SuccessfulArchiveFileRecord(
        int Id,
        string OutputPath,
        DateTimeOffset? CompletedUtc,
        DateTimeOffset CreatedUtc);
}
