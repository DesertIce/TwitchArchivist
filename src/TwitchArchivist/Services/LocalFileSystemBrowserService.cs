using System.IO;

namespace TwitchArchivist.Services;

public sealed class LocalFileSystemBrowserService : IFileSystemBrowserService
{
    public Task<IReadOnlyList<string>> GetRootDirectoriesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> roots = OperatingSystem.IsWindows()
            ? DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => EnsureTrailingSeparator(drive.RootDirectory.FullName))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [Path.DirectorySeparatorChar.ToString()];

        return Task.FromResult(roots);
    }

    public Task<IReadOnlyList<string>> GetSubdirectoriesAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A directory path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            return Task.FromResult((IReadOnlyList<string>)[]);
        }

        var directories = Directory.GetDirectories(fullPath)
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult((IReadOnlyList<string>)directories);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
