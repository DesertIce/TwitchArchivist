using System.Globalization;

namespace TwitchArchivist.Services.Twitch;

/// <summary>
/// Twitch Helix exposes broadcaster and user identifiers as decimal digit strings (snowflake ids).
/// </summary>
public static class TwitchHelixUserIds
{
    public static bool IsHelixUserId(string? value)
        => !string.IsNullOrEmpty(value) &&
           ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
