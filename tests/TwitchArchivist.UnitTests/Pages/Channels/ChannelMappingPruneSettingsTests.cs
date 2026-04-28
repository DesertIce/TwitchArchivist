using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TwitchArchivist.Pages.Channels;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.UnitTests.Pages.Channels;

public class ChannelMappingPruneSettingsTests
{
    [Fact]
    public async Task CreateModelPersistsAutoPruneSettings()
    {
        await using var database = await CreateDatabaseAsync();
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            await using var scope = database.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
            var model = new CreateModel(dbContext)
            {
                Input = new CreateModel.InputModel
                {
                    TwitchLogin = "Alpha",
                    OutputDirectory = outputDirectory,
                    AutoPruneEnabled = true,
                    AutoPruneVodCount = 7
                }
            };

            var result = await model.OnPostAsync();

            var redirect = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
            Assert.Equal("/Channels/Index", redirect.PageName);

            var channel = await dbContext.ChannelConfigurations.SingleAsync();
            Assert.True(channel.AutoPruneEnabled);
            Assert.Equal(7, channel.AutoPruneVodCount);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EditModelLoadsAndSavesAutoPruneSettings()
    {
        await using var database = await CreateDatabaseAsync();
        var initialDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var updatedDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            int channelId;
            await using (var scope = database.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
                var channel = new ChannelConfiguration
                {
                    TwitchLogin = "alpha",
                    OutputDirectory = initialDirectory,
                    IsEnabled = true,
                    AutoPruneEnabled = false,
                    AutoPruneVodCount = 5,
                    CreatedUtc = DateTimeOffset.UtcNow.AddDays(-2),
                    UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1)
                };
                dbContext.ChannelConfigurations.Add(channel);
                await dbContext.SaveChangesAsync();
                channelId = channel.Id;
            }

            await using (var scope = database.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
                var model = new EditModel(dbContext);

                var getResult = await model.OnGetAsync(channelId);
                Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(getResult);
                Assert.False(model.Input.AutoPruneEnabled);
                Assert.Equal(5, model.Input.AutoPruneVodCount);

                model.Input = new EditModel.InputModel
                {
                    Id = channelId,
                    TwitchLogin = "alpha",
                    OutputDirectory = updatedDirectory,
                    IsEnabled = true,
                    AutoPruneEnabled = true,
                    AutoPruneVodCount = 3
                };

                var postResult = await model.OnPostAsync();
                var redirect = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(postResult);
                Assert.Equal("/Channels/Index", redirect.PageName);
            }

            await using (var scope = database.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
                var channel = await dbContext.ChannelConfigurations.SingleAsync();
                Assert.True(channel.AutoPruneEnabled);
                Assert.Equal(3, channel.AutoPruneVodCount);
                Assert.Equal(updatedDirectory, channel.OutputDirectory);
            }
        }
        finally
        {
            if (Directory.Exists(initialDirectory))
            {
                Directory.Delete(initialDirectory, recursive: true);
            }

            if (Directory.Exists(updatedDirectory))
            {
                Directory.Delete(updatedDirectory, recursive: true);
            }
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
