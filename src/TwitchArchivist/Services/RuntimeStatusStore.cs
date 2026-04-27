namespace TwitchArchivist.Services;

public class RuntimeStatusStore
{
    public bool DatabaseReady { get; private set; }

    public bool DownloaderExecutableValid { get; private set; }

    public string? DownloaderExecutablePath { get; private set; }

    public string? DiagnosticsMessage { get; private set; }

    public string EventSubConnectionState { get; private set; } = "not-configured";

    public bool TwitchUserAuthorizationConfigured { get; private set; }

    public bool TwitchUserAuthorizationIsValid { get; private set; }

    public string TwitchUserAuthorizationValidity { get; private set; } = "missing";

    public string? TwitchUserAuthorizationDetail { get; private set; }

    public string? TwitchUserAuthorizationLogin { get; private set; }

    public DateTimeOffset? TwitchUserAuthorizationExpiresUtc { get; private set; }

    public DateTimeOffset? TwitchUserAuthorizationLastValidatedUtc { get; private set; }

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

    public void UpdateTwitchUserAuthorization(
        bool isConfigured,
        bool isValid,
        string validity,
        string? detail,
        string? login,
        DateTimeOffset? expiresUtc,
        DateTimeOffset? lastValidatedUtc)
    {
        TwitchUserAuthorizationConfigured = isConfigured;
        TwitchUserAuthorizationIsValid = isValid;
        TwitchUserAuthorizationValidity = validity;
        TwitchUserAuthorizationDetail = detail;
        TwitchUserAuthorizationLogin = login;
        TwitchUserAuthorizationExpiresUtc = expiresUtc;
        TwitchUserAuthorizationLastValidatedUtc = lastValidatedUtc;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }
}
