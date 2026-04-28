namespace TwitchArchivist.Services.Twitch;

public interface ICommandLineRunner
{
    Task<CommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
