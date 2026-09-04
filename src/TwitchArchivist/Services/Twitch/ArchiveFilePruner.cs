using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public static class ArchiveFilePruner
{
    private static readonly SemaphoreSlim PruneLock = new(1, 1);

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

        await PruneLock.WaitAsync(cancellationToken);
        try
        {
            await PruneCoreAsync(dbContext, channel, logger, cancellationToken);
        }
        finally
        {
            PruneLock.Release();
        }
    }

    private static async Task PruneCoreAsync(
        TwitchArchivistDbContext dbContext,
        ChannelConfiguration channel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetFullPath(channel.OutputDirectory);
        var channelFilenamePrefix = ArchiveOutputPathBuilder.GetChannelFilenamePrefix(channel.TwitchLogin, channel.Alias);
        var successfulJobs = await dbContext.ArchiveJobs
            .Where(x => x.ChannelConfigurationId == channel.Id &&
                        x.Status == ArchiveJobStatus.Succeeded &&
                        x.OutputPath != null)
            .ToListAsync(cancellationToken);

        var existingFiles = successfulJobs
            .OrderByDescending(x => x.CompletedUtc ?? x.CreatedUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => ResolveExistingArchiveFile(x, outputDirectory, channelFilenamePrefix))
            .OfType<ExistingArchiveFile>()
            .ToList();

        var uncompressedRetentionCount = Math.Max(0, channel.AutoPruneVodCount);
        var compressedRetentionCount = channel.CompressEnabled
            ? Math.Max(0, channel.CompressVodCount)
            : 0;

        foreach (var file in existingFiles.Take(uncompressedRetentionCount))
        {
            file.Job.OutputPath = file.FullPath;
        }

        foreach (var file in existingFiles
                     .Skip(uncompressedRetentionCount)
                     .Take(compressedRetentionCount))
        {
            if (file.IsCompressed)
            {
                file.Job.OutputPath = file.FullPath;
                continue;
            }

            try
            {
                var compressedPath = await CompressFileAsync(file.FullPath, cancellationToken);
                file.Job.OutputPath = compressedPath;
                logger.LogInformation(
                    "Compressed archived VOD file for channel {ChannelLogin}: {OutputPath}",
                    channel.TwitchLogin,
                    compressedPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to compress archived VOD file for channel {ChannelLogin}: {OutputPath}",
                    channel.TwitchLogin,
                    file.FullPath);
            }
        }

        foreach (var file in existingFiles.Skip(uncompressedRetentionCount + compressedRetentionCount))
        {
            try
            {
                File.Delete(file.FullPath);
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

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ExistingArchiveFile? ResolveExistingArchiveFile(
        ArchiveJob job,
        string rootDirectory,
        string channelFilenamePrefix)
    {
        var fullPath = NormalizeIfUnderRootAndMatchChannelPrefix(
            job.OutputPath!,
            rootDirectory,
            channelFilenamePrefix);
        if (fullPath is null)
        {
            return null;
        }

        if (File.Exists(fullPath))
        {
            return new ExistingArchiveFile(job, fullPath, IsGzipPath(fullPath));
        }

        if (IsGzipPath(fullPath))
        {
            return null;
        }

        var compressedPath = NormalizeIfUnderRootAndMatchChannelPrefix(
            $"{fullPath}.gz",
            rootDirectory,
            channelFilenamePrefix);
        return compressedPath is not null && File.Exists(compressedPath)
            ? new ExistingArchiveFile(job, compressedPath, IsCompressed: true)
            : null;
    }

    private static async Task<string> CompressFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var compressedPath = $"{sourcePath}.gz";
        var temporaryPath = $"{compressedPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: false))
            {
                await source.CopyToAsync(gzip, cancellationToken);
            }

            File.Move(temporaryPath, compressedPath, overwrite: true);
            File.Delete(sourcePath);
            return compressedPath;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A later prune pass uses a unique temporary path and can retry the compression.
        }
    }

    private static bool IsGzipPath(string path)
    {
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
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

    private sealed record ExistingArchiveFile(
        ArchiveJob Job,
        string FullPath,
        bool IsCompressed);
}
