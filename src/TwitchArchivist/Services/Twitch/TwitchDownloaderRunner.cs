using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;

namespace TwitchArchivist.Services.Twitch;

public class TwitchDownloaderRunner(
    IOptionsMonitor<DownloaderOptions> downloaderOptions) : ITwitchDownloaderRunner
{
    private const int WatchdogExitCode = -2;

    public async Task<TwitchDownloaderResult> DownloadVideoAsync(string vodId, string outputPath, CancellationToken cancellationToken)
    {
        var options = downloaderOptions.CurrentValue;
        var executablePath = DownloaderExecutablePathResolver.Resolve(options.ExecutablePath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new TwitchDownloaderResult(false, -1, string.Empty, "TwitchDownloaderCLI executable path is not configured or does not exist.");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("videodownload");
        process.StartInfo.ArgumentList.Add("--id");
        process.StartInfo.ArgumentList.Add(vodId);
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add("--collision");
        process.StartInfo.ArgumentList.Add("Overwrite");

        process.Start();

        var tracker = new DownloadProgressTracker(outputPath);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var watchdogFailureState = new WatchdogFailureState();
        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdoutTask = ReadOutputAsync(process.StandardOutput, stdout, tracker, cancellationToken);
        var stderrTask = ReadOutputAsync(process.StandardError, stderr, tracker, cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        var watchdogTask = WatchForInactivityAsync(
            process,
            outputPath,
            tracker,
            watchdogFailureState,
            ResolveDownloadInactivityTimeout(options),
            ResolveDownloadWatchInterval(options),
            watchdogCancellation.Token);

        try
        {
            var completedTask = await Task.WhenAny(exitTask, watchdogTask);
            if (completedTask == watchdogTask)
            {
                await watchdogTask;
                if (watchdogFailureState.TryGetMessage(out var watchdogFailure))
                {
                    await exitTask;
                    await Task.WhenAll(stdoutTask, stderrTask);
                    return new TwitchDownloaderResult(
                        false,
                        WatchdogExitCode,
                        stdout.ToString(),
                        AppendFailure(stderr.ToString(), watchdogFailure));
                }
            }

            await exitTask;
            watchdogCancellation.Cancel();
            await IgnoreCancellationAsync(watchdogTask);
            await Task.WhenAll(stdoutTask, stderrTask);
            if (watchdogFailureState.TryGetMessage(out var exitRaceWatchdogFailure))
            {
                return new TwitchDownloaderResult(
                    false,
                    WatchdogExitCode,
                    stdout.ToString(),
                    AppendFailure(stderr.ToString(), exitRaceWatchdogFailure));
            }

            return new TwitchDownloaderResult(process.ExitCode == 0, process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TimeSpan ResolveDownloadInactivityTimeout(DownloaderOptions options) =>
        TimeSpan.FromSeconds(Math.Max(1, options.DownloadInactivityTimeoutSeconds));

    private static TimeSpan ResolveDownloadWatchInterval(DownloaderOptions options)
    {
        var inactivityTimeoutSeconds = Math.Max(1, options.DownloadInactivityTimeoutSeconds);
        var watchIntervalSeconds = Math.Max(1, options.DownloadWatchIntervalSeconds);
        return TimeSpan.FromSeconds(Math.Min(watchIntervalSeconds, inactivityTimeoutSeconds));
    }

    private static async Task ReadOutputAsync(
        TextReader reader,
        StringBuilder builder,
        DownloadProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                return;
            }

            tracker.RecordActivity();
            builder.Append(buffer, 0, read);
        }
    }

    private static async Task<string?> WatchForInactivityAsync(
        Process process,
        string outputPath,
        DownloadProgressTracker tracker,
        WatchdogFailureState watchdogFailureState,
        TimeSpan inactivityTimeout,
        TimeSpan watchInterval,
        CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            await Task.Delay(watchInterval, cancellationToken);
            tracker.RecordFileActivity(outputPath);
            if (process.HasExited)
            {
                return null;
            }

            var idleFor = DateTimeOffset.UtcNow - tracker.LastActivityUtc;
            if (idleFor < inactivityTimeout)
            {
                continue;
            }

            watchdogFailureState.SetMessage(
                $"TwitchDownloaderCLI did not report progress for {FormatDuration(idleFor)} and was terminated.");
            KillProcessTree(process);
            return watchdogFailureState.Message;
        }

        return null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string AppendFailure(string standardError, string failure)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return failure;
        }

        return $"{standardError.TrimEnd()}{Environment.NewLine}{failure}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s";
        }

        return $"{Math.Max(1, (int)duration.TotalSeconds)}s";
    }

    private sealed class DownloadProgressTracker(string outputPath)
    {
        private readonly object _sync = new();
        private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
        private FileSnapshot _lastOutputSnapshot = FileSnapshot.Capture(outputPath);

        public DateTimeOffset LastActivityUtc
        {
            get
            {
                lock (_sync)
                {
                    return _lastActivityUtc;
                }
            }
        }

        public void RecordActivity()
        {
            lock (_sync)
            {
                _lastActivityUtc = DateTimeOffset.UtcNow;
            }
        }

        public void RecordFileActivity(string currentOutputPath)
        {
            var snapshot = FileSnapshot.Capture(currentOutputPath);
            lock (_sync)
            {
                if (snapshot == _lastOutputSnapshot)
                {
                    return;
                }

                _lastOutputSnapshot = snapshot;
                _lastActivityUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private sealed class WatchdogFailureState
    {
        private readonly object _sync = new();
        private string? _message;

        public string? Message
        {
            get
            {
                lock (_sync)
                {
                    return _message;
                }
            }
        }

        public void SetMessage(string message)
        {
            lock (_sync)
            {
                _message = message;
            }
        }

        public bool TryGetMessage(out string message)
        {
            lock (_sync)
            {
                message = _message ?? string.Empty;
                return !string.IsNullOrWhiteSpace(_message);
            }
        }
    }

    private sealed record FileSnapshot(bool Exists, long Length, DateTime LastWriteTimeUtc)
    {
        public static FileSnapshot Capture(string path)
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Exists
                ? new FileSnapshot(true, fileInfo.Length, fileInfo.LastWriteTimeUtc)
                : new FileSnapshot(false, 0, DateTime.MinValue);
        }
    }
}
