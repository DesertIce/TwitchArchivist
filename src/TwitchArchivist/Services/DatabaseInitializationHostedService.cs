using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence;

namespace TwitchArchivist.Services;

public class DatabaseInitializationHostedService(
    IServiceScopeFactory scopeFactory,
    RuntimeStatusStore runtimeStatusStore,
    ILogger<DatabaseInitializationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();

        logger.LogInformation("Applying database migrations");
        await dbContext.Database.MigrateAsync(cancellationToken);

        runtimeStatusStore.MarkDatabaseReady();
        logger.LogInformation("Database is ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
