using System.Diagnostics;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Services.Twitch;

namespace TwitchArchivist.UnitTests.Services.Twitch;

public class TwitchDownloaderRunnerTests
{
    [Fact]
    public async Task DownloadVideoAsync_FailsAndTerminatesDownloaderWhenNoProgressIsObserved()
    {
        using var tempDirectory = new TempDirectory();
        var executablePath = CreateHungDownloader(tempDirectory.Path);
        var outputPath = Path.Combine(tempDirectory.Path, "archive.mp4");
        var runner = new TwitchDownloaderRunner(new FakeOptionsMonitor(new DownloaderOptions
        {
            ExecutablePath = executablePath,
            DownloadInactivityTimeoutSeconds = 1,
            DownloadWatchIntervalSeconds = 1
        }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await runner.DownloadVideoAsync("vod-1", outputPath, timeout.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(-2, result.ExitCode);
        Assert.Contains("TwitchDownloaderCLI did not report progress", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadVideoAsync_TreatsOutputFileChangesAsProgress()
    {
        using var tempDirectory = new TempDirectory();
        var executablePath = CreateWritingDownloader(tempDirectory.Path);
        var outputPath = Path.Combine(tempDirectory.Path, "archive.mp4");
        var runner = new TwitchDownloaderRunner(new FakeOptionsMonitor(new DownloaderOptions
        {
            ExecutablePath = executablePath,
            DownloadInactivityTimeoutSeconds = 1,
            DownloadWatchIntervalSeconds = 1
        }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await runner.DownloadVideoAsync("vod-1", outputPath, timeout.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.True(new FileInfo(outputPath).Length > 0);
    }

    [Fact]
    public async Task DownloadVideoAsync_UsesOverwriteCollisionHandling()
    {
        using var tempDirectory = new TempDirectory();
        var executablePath = CreateArgumentRecordingDownloader(tempDirectory.Path);
        var outputPath = Path.Combine(tempDirectory.Path, "archive.mp4");
        var argumentPath = outputPath + ".args";
        var runner = new TwitchDownloaderRunner(new FakeOptionsMonitor(new DownloaderOptions
        {
            ExecutablePath = executablePath,
            DownloadInactivityTimeoutSeconds = 5,
            DownloadWatchIntervalSeconds = 1
        }));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await runner.DownloadVideoAsync("vod-1", outputPath, timeout.Token);

        Assert.True(result.Succeeded);
        var arguments = await File.ReadAllLinesAsync(argumentPath, timeout.Token);
        var collisionIndex = Array.IndexOf(arguments, "--collision");
        Assert.True(collisionIndex >= 0, string.Join(" ", arguments));
        Assert.Equal("Overwrite", arguments[collisionIndex + 1]);
    }

    private static string CreateHungDownloader(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateHungDotnetExecutable(directoryPath);
        }
        else
        {
            var path = Path.Combine(directoryPath, "hung-downloader");
            File.WriteAllText(
                path,
                """
                #!/usr/bin/env bash
                while true; do
                  sleep 1
                done
                """);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            return path;
        }
    }

    private static string CreateWritingDownloader(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateDotnetExecutable(
                directoryPath,
                "WritingDownloader",
                """
                var outputPath = string.Empty;
                for (var index = 0; index < args.Length - 1; index += 1)
                {
                    if (args[index] == "-o")
                    {
                        outputPath = args[index + 1];
                    }
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Environment.Exit(2);
                    return;
                }

                for (var index = 0; index < 4; index += 1)
                {
                    await File.AppendAllTextAsync(outputPath, $"chunk-{index}{Environment.NewLine}");
                    await Task.Delay(TimeSpan.FromMilliseconds(400));
                }
                """);
        }
        else
        {
            var path = Path.Combine(directoryPath, "writing-downloader");
            File.WriteAllText(
                path,
                """
                #!/usr/bin/env bash
                output_path=""
                while [[ $# -gt 0 ]]; do
                  case "$1" in
                    -o)
                      output_path="$2"
                      shift 2
                      ;;
                    *)
                      shift
                      ;;
                  esac
                done
                for index in 0 1 2 3; do
                  printf 'chunk-%s\n' "$index" >> "$output_path"
                  sleep 0.4
                done
                """);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            return path;
        }
    }

    private static string CreateArgumentRecordingDownloader(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateDotnetExecutable(
                directoryPath,
                "ArgumentRecordingDownloader",
                """
                var outputPath = string.Empty;
                for (var index = 0; index < args.Length - 1; index += 1)
                {
                    if (args[index] == "-o")
                    {
                        outputPath = args[index + 1];
                    }
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Environment.Exit(2);
                    return;
                }

                await File.WriteAllLinesAsync(outputPath + ".args", args);
                """);
        }
        else
        {
            var path = Path.Combine(directoryPath, "argument-recording-downloader");
            File.WriteAllText(
                path,
                """
                #!/usr/bin/env bash
                output_path=""
                args=("$@")
                while [[ $# -gt 0 ]]; do
                  case "$1" in
                    -o)
                      output_path="$2"
                      shift 2
                      ;;
                    *)
                      shift
                      ;;
                  esac
                done
                printf '%s\n' "${args[@]}" > "${output_path}.args"
                """);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            return path;
        }
    }

    private static string CreateHungDotnetExecutable(string directoryPath)
    {
        return CreateDotnetExecutable(
            directoryPath,
            "HungDownloader",
            """
            await Task.Delay(TimeSpan.FromHours(1));
            """);
    }

    private static string CreateDotnetExecutable(string directoryPath, string projectName, string source)
    {
        var projectDirectory = Path.Combine(directoryPath, projectName);
        var publishDirectory = Path.Combine(directoryPath, $"{projectName}-publish");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, $"{projectName}.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), source);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = projectDirectory
            }
        };
        process.StartInfo.ArgumentList.Add("publish");
        process.StartInfo.ArgumentList.Add("--nologo");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("q");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add(publishDirectory);

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to build hung downloader helper.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        return Path.Combine(publishDirectory, $"{projectName}.exe");
    }

    private sealed class FakeOptionsMonitor(DownloaderOptions currentValue) : IOptionsMonitor<DownloaderOptions>
    {
        public DownloaderOptions CurrentValue => currentValue;

        public DownloaderOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<DownloaderOptions, string?> listener) => null;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
