using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Pages;

internal static class UiBadges
{
    public static string JobStatusBadgeClass(ArchiveJobStatus status) => status switch
    {
        ArchiveJobStatus.Succeeded => "success",
        ArchiveJobStatus.Failed => "danger",
        ArchiveJobStatus.Running => "info",
        ArchiveJobStatus.WaitingForVod => "warn",
        ArchiveJobStatus.Pending => "neutral",
        ArchiveJobStatus.Skipped => "neutral",
        _ => "neutral"
    };

    public static string JobStatusLabel(ArchiveJobStatus status) => status switch
    {
        ArchiveJobStatus.WaitingForVod => "Waiting for VOD",
        _ => status.ToString()
    };

    public static string EventSubBadgeClass(string state) => state switch
    {
        "connected" => "success",
        "reconnected" => "success",
        "connecting" => "warn",
        "awaiting-authorization" => "warn",
        "not-configured" => "neutral",
        "disconnected" => "danger",
        "error" => "danger",
        _ => "neutral"
    };

    public static string BoolBadgeClass(bool value) => value ? "success" : "danger";
}
