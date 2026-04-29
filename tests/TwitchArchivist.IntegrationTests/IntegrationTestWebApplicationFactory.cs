using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TwitchArchivist.IntegrationTests;

internal sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly Action<IWebHostBuilder>? _configureBuilder;
    private readonly string _databasePath;
    private readonly IReadOnlyDictionary<string, string?> _configurationOverrides;
    private readonly bool _ownsDatabasePath;

    public IntegrationTestWebApplicationFactory(
        Action<IWebHostBuilder>? configureBuilder = null,
        string? databasePath = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        _configureBuilder = configureBuilder;
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(Path.GetTempPath(), "TwitchArchivist.IntegrationTests", $"{Guid.NewGuid():N}.db")
            : databasePath;
        _configurationOverrides = configurationOverrides ?? new Dictionary<string, string?>();
        _ownsDatabasePath = string.IsNullOrWhiteSpace(databasePath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            var settings = new Dictionary<string, string?>(_configurationOverrides, StringComparer.OrdinalIgnoreCase)
            {
                ["Storage:DatabasePath"] = _databasePath
            };

            configurationBuilder.AddInMemoryCollection(settings);
        });

        _configureBuilder?.Invoke(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        DeleteDatabaseFiles();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DeleteDatabaseFiles();
    }

    private void DeleteDatabaseFiles()
    {
        if (!_ownsDatabasePath)
        {
            return;
        }

        TryDelete(_databasePath);
        TryDelete($"{_databasePath}-wal");
        TryDelete($"{_databasePath}-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
