namespace TwitchArchivist.Services;

public interface IFileSystemBrowserService
{
    Task<IReadOnlyList<string>> GetRootDirectoriesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetSubdirectoriesAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileSystemBrowserEntry>> GetEntriesAsync(
        string path,
        bool includeFiles,
        string? searchPattern,
        CancellationToken cancellationToken);
}
