using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TwitchArchivist.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTwitchArchivistPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        var databasePath = configuration["Storage:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(contentRootPath, "data", "twitcharchivist.db");
        }

        var fullDatabasePath = Path.GetFullPath(databasePath, contentRootPath);
        var directory = Path.GetDirectoryName(fullDatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite($"Data Source={fullDatabasePath}"));

        return services;
    }
}
