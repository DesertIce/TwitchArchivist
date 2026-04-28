using System.Globalization;

namespace TwitchArchivist.Services.Logging;

public sealed class RollingFileLogStore
{
    private readonly object _sync = new();
    private readonly string _directoryPath;
    private readonly string _filePrefix;
    private readonly int _retainedDayCount;
    private readonly TimeProvider _clock;

    public RollingFileLogStore(string directoryPath, string filePrefix, int retainedDayCount, TimeProvider? clock = null)
    {
        _directoryPath = directoryPath;
        _filePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "twitcharchivist" : filePrefix.Trim();
        _retainedDayCount = Math.Max(1, retainedDayCount);
        _clock = clock ?? TimeProvider.System;

        Directory.CreateDirectory(_directoryPath);
    }

    public void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_sync)
        {
            var utcNow = _clock.GetUtcNow();
            var currentDate = DateOnly.FromDateTime(utcNow.UtcDateTime);
            var logFilePath = Path.Combine(_directoryPath, $"{_filePrefix}-{currentDate:yyyy-MM-dd}.log");
            File.AppendAllText(logFilePath, line + Environment.NewLine);
            PruneOldFiles(currentDate);
        }
    }

    private void PruneOldFiles(DateOnly currentDate)
    {
        var cutoffDate = currentDate.AddDays(-(_retainedDayCount - 1));
        foreach (var path in Directory.GetFiles(_directoryPath, $"{_filePrefix}-*.log"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var dateSegment = fileName[_filePrefix.Length..].TrimStart('-');
            if (!DateOnly.TryParseExact(dateSegment, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                continue;
            }

            if (parsedDate < cutoffDate)
            {
                File.Delete(path);
            }
        }
    }
}
