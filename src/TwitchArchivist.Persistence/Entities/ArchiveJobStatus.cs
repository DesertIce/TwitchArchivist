namespace TwitchArchivist.Persistence.Entities;

public enum ArchiveJobStatus
{
    Pending = 0,
    WaitingForVod = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Skipped = 5
}
