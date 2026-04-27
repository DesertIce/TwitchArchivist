namespace TwitchArchivist.Models;

public class StorageOptions
{
    public const string SectionName = "Storage";

    public string DatabasePath { get; set; } = "data/twitcharchivist.db";
}
