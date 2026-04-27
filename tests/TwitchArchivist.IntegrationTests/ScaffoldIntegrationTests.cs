namespace TwitchArchivist.IntegrationTests;

public class ScaffoldIntegrationTests
{
    [Fact]
    public void PersistenceExtensionIsAvailable()
    {
        var extensionType = typeof(TwitchArchivist.Persistence.ServiceCollectionExtensions);

        Assert.Equal("ServiceCollectionExtensions", extensionType.Name);
    }
}
