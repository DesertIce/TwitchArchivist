using System.Diagnostics;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;

namespace TwitchArchivist.Services.Twitch;

public class TwitchDownloaderRunner(
    IOptionsMonitor<DownloaderOptions> downloaderOptions) : ITwitchDownloaderRunner
{
    public async Task<TwitchDownloaderResult> DownloadVideoAsync(string vodId, string outputPath, CancellationToken cancellationToken)
    {
        var executablePath = DownloaderExecutablePathResolver.Resolve(downloaderOptions.CurrentValue.ExecutablePath);
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
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add("videodownload");
        process.StartInfo.ArgumentList.Add("--id");
        process.StartInfo.ArgumentList.Add(vodId);
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(outputPath);

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new TwitchDownloaderResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
    }
}
