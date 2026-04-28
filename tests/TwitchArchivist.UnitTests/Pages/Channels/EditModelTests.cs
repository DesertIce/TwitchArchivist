using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchArchivist.Pages.Channels;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.UnitTests.Pages.Channels;

public class EditModelTests
{
    [Fact]
    public async Task OnPostDeleteAsyncRemovesChannelAndCascadeData()
    {
        await using var database = await CreateDatabaseAsync();
        int channelId;

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var channel = new ChannelConfiguration
            {
                TwitchLogin = "delete-target",
                TwitchUserId = "12345",
                OutputDirectory = @"D:\archive\delete-target",
                IsEnabled = true,
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
            };

            dbContext.ChannelConfigurations.Add(channel);
            await dbContext.SaveChangesAsync();
            channelId = channel.Id;

            dbContext.EventSubscriptionStates.Add(new EventSubscriptionState
            {
                ChannelConfigurationId = channelId,
                SubscriptionType = "stream.online",
                TwitchSubscriptionId = "sub-1",
                Status = "enabled",
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
            });
            dbContext.StreamSessionStates.Add(new StreamSessionState
            {
                ChannelConfigurationId = channelId,
                LastKnownStreamId = "stream-1",
                LastOnlineUtc = DateTimeOffset.UtcNow.AddHours(-4),
                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedUtc = DateTimeOffset.UtcNow.AddHours(-4)
            });
            dbContext.ArchiveJobs.Add(new ArchiveJob
            {
                ChannelConfigurationId = channelId,
                TriggerSource = "stream.offline",
                Status = ArchiveJobStatus.Succeeded,
                CreatedUtc = DateTimeOffset.UtcNow.AddHours(-3)
            });
            await dbContext.SaveChangesAsync();
        }

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var model = new EditModel(dbContext)
            {
                Input = new EditModel.InputModel
                {
                    Id = channelId
                }
            };

            var result = await model.OnPostDeleteAsync();

            var redirect = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
            Assert.Equal("/Channels/Index", redirect.PageName);
        }

        await using (var scope = database.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            Assert.False(await dbContext.ChannelConfigurations.AnyAsync());
            Assert.False(await dbContext.EventSubscriptionStates.AnyAsync());
            Assert.False(await dbContext.StreamSessionStates.AnyAsync());
            Assert.False(await dbContext.ArchiveJobs.AnyAsync());
        }
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TwitchArchivistDbContext>(options => options.UseSqlite(connection));

        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new TestDatabase(provider, connection);
    }

    private sealed class TestDatabase(IServiceProvider services, SqliteConnection connection) : IAsyncDisposable
    {
        public IServiceProvider Services { get; } = services;

        public async ValueTask DisposeAsync()
        {
            if (Services is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await connection.DisposeAsync();
        }
    }
}
