namespace TwitchArchivist.Models;

public class FileLoggingOptions
{
    public const string SectionName = "FileLogging";

    public string DirectoryPath { get; set; } = "logs";

    public string FilePrefix { get; set; } = "twitcharchivist";

    public int RetainedDayCount { get; set; } = 3;
}
