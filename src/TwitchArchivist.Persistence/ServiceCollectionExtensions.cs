using Microsoft.Extensions.DependencyInjection;

namespace TwitchArchivist.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTwitchArchivistPersistence(this IServiceCollection services)
    {
        return services;
    }
}
