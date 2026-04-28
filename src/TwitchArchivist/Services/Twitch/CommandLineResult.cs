namespace TwitchArchivist.Services.Twitch;

public sealed record CommandLineResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
