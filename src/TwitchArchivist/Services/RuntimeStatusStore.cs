namespace TwitchArchivist.Services;

public class RuntimeStatusStore
{
    public bool DatabaseReady { get; private set; }

    public bool DownloaderExecutableValid { get; private set; }

    public string? DownloaderExecutablePath { get; private set; }

    public string? DiagnosticsMessage { get; private set; }

    public string EventSubConnectionState { get; private set; } = "not-configured";

    public DateTimeOffset UpdatedUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void MarkDatabaseReady()
    {
        DatabaseReady = true;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public void UpdateDownloaderValidation(string? executablePath, bool isValid, string message)
    {
        DownloaderExecutablePath = executablePath;
        DownloaderExecutableValid = isValid;
        DiagnosticsMessage = message;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public void UpdateEventSubConnectionState(string state)
    {
        EventSubConnectionState = state;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }
}
