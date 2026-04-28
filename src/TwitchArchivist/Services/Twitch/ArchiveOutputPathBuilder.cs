using System.Text;

namespace TwitchArchivist.Services.Twitch;

public static class ArchiveOutputPathBuilder
{
    public static string GetChannelFilenamePrefix(string channelName)
    {
        var safeChannelName = SanitizeSegment(channelName);
        return string.IsNullOrWhiteSpace(safeChannelName)
            ? string.Empty
            : $"{safeChannelName}-";
    }

    public static string Build(string outputDirectory, string channelName, ArchiveVodRecord vod)
    {
        var safeChannelName = SanitizeSegment(channelName);
        var safeTitle = SanitizeSegment(vod.Title);
        var dateSegment = vod.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd");

        var filenameSegments = new List<string>
        {
            safeChannelName,
            dateSegment
        };

        if (!string.IsNullOrWhiteSpace(safeTitle))
        {
            filenameSegments.Add(safeTitle);
        }

        filenameSegments.Add(vod.Id);

        var baseFilename = string.Join("-", filenameSegments);
        var basePath = Path.Combine(outputDirectory, $"{baseFilename}.mp4");
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        var counter = 1;
        while (true)
        {
            var candidate = Path.Combine(outputDirectory, $"{baseFilename}_{counter}.mp4");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter += 1;
        }
    }

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        var lastCharacterWasSeparator = false;

        foreach (var character in value.Trim())
        {
            if (invalidFileNameChars.Contains(character) || char.IsControl(character))
            {
                if (!lastCharacterWasSeparator)
                {
                    builder.Append(' ');
                    lastCharacterWasSeparator = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastCharacterWasSeparator)
                {
                    builder.Append(' ');
                    lastCharacterWasSeparator = true;
                }

                continue;
            }

            builder.Append(character);
            lastCharacterWasSeparator = false;
        }

        return builder.ToString().Trim(' ', '.');
    }
}
