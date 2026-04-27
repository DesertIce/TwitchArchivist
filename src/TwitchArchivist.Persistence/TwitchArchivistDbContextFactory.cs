using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TwitchArchivist.Persistence;

public class TwitchArchivistDbContextFactory : IDesignTimeDbContextFactory<TwitchArchivistDbContext>
{
    public TwitchArchivistDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TwitchArchivistDbContext>();
        optionsBuilder.UseSqlite("Data Source=twitcharchivist.design.db");
        return new TwitchArchivistDbContext(optionsBuilder.Options);
    }
}
