namespace TwitchArchivist.UnitTests;

public class ScaffoldTests
{
    [Fact]
    public void ServiceEntryAssemblyExists()
    {
        var assembly = typeof(Program).Assembly;

        Assert.Equal("TwitchArchivist", assembly.GetName().Name);
    }
}
