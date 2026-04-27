using Microsoft.EntityFrameworkCore;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Persistence;

public class TwitchArchivistDbContext(DbContextOptions<TwitchArchivistDbContext> options) : DbContext(options)
{
    public DbSet<ChannelConfiguration> ChannelConfigurations => Set<ChannelConfiguration>();

    public DbSet<EventSubscriptionState> EventSubscriptionStates => Set<EventSubscriptionState>();

    public DbSet<StreamSessionState> StreamSessionStates => Set<StreamSessionState>();

    public DbSet<ArchiveJob> ArchiveJobs => Set<ArchiveJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelConfiguration>(entity =>
        {
            entity.Property(x => x.TwitchLogin).HasMaxLength(128);
            entity.Property(x => x.TwitchUserId).HasMaxLength(64);
            entity.Property(x => x.OutputDirectory).HasMaxLength(1024);
            entity.HasIndex(x => x.TwitchLogin).IsUnique();
        });

        modelBuilder.Entity<EventSubscriptionState>(entity =>
        {
            entity.Property(x => x.SubscriptionType).HasMaxLength(128);
            entity.Property(x => x.TwitchSubscriptionId).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(64);
            entity.HasIndex(x => new { x.ChannelConfigurationId, x.SubscriptionType }).IsUnique();
            entity.HasOne(x => x.ChannelConfiguration)
                .WithMany(x => x.EventSubscriptions)
                .HasForeignKey(x => x.ChannelConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StreamSessionState>(entity =>
        {
            entity.Property(x => x.LastKnownStreamId).HasMaxLength(128);
            entity.Property(x => x.LastProcessedOfflineMessageId).HasMaxLength(128);
            entity.HasIndex(x => x.ChannelConfigurationId).IsUnique();
            entity.HasOne(x => x.ChannelConfiguration)
                .WithOne(x => x.StreamSessionState)
                .HasForeignKey<StreamSessionState>(x => x.ChannelConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArchiveJob>(entity =>
        {
            entity.Property(x => x.TriggerSource).HasMaxLength(128);
            entity.Property(x => x.VodId).HasMaxLength(128);
            entity.Property(x => x.OutputPath).HasMaxLength(2048);
            entity.Property(x => x.LastError).HasMaxLength(4000);
            entity.HasOne(x => x.ChannelConfiguration)
                .WithMany(x => x.ArchiveJobs)
                .HasForeignKey(x => x.ChannelConfigurationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
