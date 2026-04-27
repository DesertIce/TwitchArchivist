using System.Diagnostics;

namespace TwitchArchivist.IntegrationTests;

public class ServiceScriptIntegrationTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void InstallScriptDefaultsPublishDirectoryUnderAppData()
    {
        var scriptPath = Path.Combine(RepositoryRoot, "scripts", "install-service.ps1");
        var expectedRoot = Path.Combine(Path.GetTempPath(), $"TwitchArchivist-AppData-{Guid.NewGuid():N}");
        var expectedPublishDirectory = Path.Combine(expectedRoot, "TwitchArchivist");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                "-ServiceName TestTwitchArchivist " +
                "-WhatIf",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot,
        };
        startInfo.Environment["APPDATA"] = expectedRoot;

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        process!.WaitForExit();

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();

        Assert.True(
            process.ExitCode == 0,
            $"Expected install script to succeed in dry-run mode.{Environment.NewLine}" +
            $"StdOut:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
            $"StdErr:{Environment.NewLine}{standardError}");
        Assert.Contains(expectedPublishDirectory, standardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallScriptDryRunHandlesSingleExistingAppSettingsFile()
    {
        var scriptPath = Path.Combine(RepositoryRoot, "scripts", "install-service.ps1");
        var publishDirectory = Path.Combine(Path.GetTempPath(), $"TwitchArchivist-Publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(publishDirectory);
        File.WriteAllText(Path.Combine(publishDirectory, "appsettings.json"), "{ }");

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                "-ServiceName TestTwitchArchivist " +
                $"-PublishDirectory \"{publishDirectory}\" " +
                "-WhatIf",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        process!.WaitForExit();

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();

        try
        {
            Assert.True(
                process.ExitCode == 0,
                $"Expected install script to succeed when one appsettings file exists.{Environment.NewLine}" +
                $"StdOut:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
                $"StdErr:{Environment.NewLine}{standardError}");
        }
        finally
        {
            Directory.Delete(publishDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("install-service.ps1")]
    [InlineData("update-service.ps1")]
    [InlineData("uninstall-service.ps1")]
    public void ServiceScriptsSupportDryRunExecution(string scriptName)
    {
        var scriptPath = Path.Combine(RepositoryRoot, "scripts", scriptName);

        Assert.True(File.Exists(scriptPath), $"Expected script to exist: {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                "-ServiceName TestTwitchArchivist " +
                $"-PublishDirectory \"{Path.Combine(RepositoryRoot, "publish", "test")}\" " +
                "-WhatIf",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        process!.WaitForExit();

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();

        Assert.True(
            process.ExitCode == 0,
            $"Expected script {scriptName} to succeed in dry-run mode.{Environment.NewLine}" +
            $"StdOut:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
            $"StdErr:{Environment.NewLine}{standardError}");
    }
}
