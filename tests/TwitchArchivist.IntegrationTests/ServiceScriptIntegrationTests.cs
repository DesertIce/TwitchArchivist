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

    [Theory]
    [InlineData("install-service.ps1", false)]
    [InlineData("update-service.ps1", false)]
    [InlineData("uninstall-service.ps1", true)]
    public void ServiceScriptsSkipPublishOutsideGitDirectory(string scriptName, bool removePublishDirectory)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"TwitchArchivist-ReleaseBundle-{Guid.NewGuid():N}");
        var tempScriptsDirectory = Path.Combine(tempRoot, "scripts");
        Directory.CreateDirectory(tempScriptsDirectory);

        foreach (var sourcePath in Directory.GetFiles(Path.Combine(RepositoryRoot, "scripts"), "*.ps1"))
        {
            File.Copy(sourcePath, Path.Combine(tempScriptsDirectory, Path.GetFileName(sourcePath)));
        }

        var scriptPath = Path.Combine(tempScriptsDirectory, scriptName);
        var arguments =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
            "-ServiceName TestTwitchArchivist " +
            (removePublishDirectory ? "-RemovePublishDirectory " : string.Empty) +
            "-WhatIf";

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = tempRoot,
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
                $"Expected script {scriptName} to succeed outside a git directory.{Environment.NewLine}" +
                $"StdOut:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
                $"StdErr:{Environment.NewLine}{standardError}");

            Assert.DoesNotContain("Publish service binaries from", standardOutput, StringComparison.OrdinalIgnoreCase);

            if (removePublishDirectory)
            {
                Assert.Contains(tempRoot, standardOutput, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void StagedPublishPromotesApplicationFilesAndPreservesRuntimeData()
    {
        var fixture = CreateStagedPublishFixture();
        try
        {
            var result = RunStagedPublishScript(
                fixture,
                """
                $itemNames = @(Get-PublishItemNames `
                    -StagingDirectory $stagingDirectory `
                    -PublishDirectory $publishDirectory)
                Move-StagedPublishIntoPlace `
                    -StagingDirectory $stagingDirectory `
                    -PublishDirectory $publishDirectory `
                    -BackupDirectory $backupDirectory `
                    -ItemNames $itemNames
                """);

            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Equal("new application", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "TwitchArchivist.dll")));
            Assert.Equal("new dependency", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "NewDependency.dll")));
            Assert.False(File.Exists(Path.Combine(fixture.PublishDirectory, "RemovedDependency.dll")));
            Assert.Equal("old application", File.ReadAllText(Path.Combine(fixture.BackupDirectory, "TwitchArchivist.dll")));
            Assert.Equal("removed dependency", File.ReadAllText(Path.Combine(fixture.BackupDirectory, "RemovedDependency.dll")));
            Assert.Equal("operator settings", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "appsettings.json")));
            Assert.Equal("database", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "data", "twitcharchivist.db")));
            Assert.Equal("log", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "logs", "service.log")));
            Assert.Equal("tool", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "tools", "tool.exe")));
            Assert.True(File.Exists(Path.Combine(fixture.StagingDirectory, "appsettings.json")));
            Assert.True(Directory.Exists(Path.Combine(fixture.StagingDirectory, "data")));
        }
        finally
        {
            Directory.Delete(fixture.RootDirectory, recursive: true);
        }
    }

    [Fact]
    public void StagedPublishRollbackRestoresPreviousApplicationFiles()
    {
        var fixture = CreateStagedPublishFixture();
        try
        {
            var result = RunStagedPublishScript(
                fixture,
                """
                $itemNames = @(Get-PublishItemNames `
                    -StagingDirectory $stagingDirectory `
                    -PublishDirectory $publishDirectory)
                Move-StagedPublishIntoPlace `
                    -StagingDirectory $stagingDirectory `
                    -PublishDirectory $publishDirectory `
                    -BackupDirectory $backupDirectory `
                    -ItemNames $itemNames
                Restore-PublishBackup `
                    -StagingDirectory $stagingDirectory `
                    -PublishDirectory $publishDirectory `
                    -BackupDirectory $backupDirectory `
                    -ItemNames $itemNames
                """);

            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Equal("old application", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "TwitchArchivist.dll")));
            Assert.False(File.Exists(Path.Combine(fixture.PublishDirectory, "NewDependency.dll")));
            Assert.Equal("removed dependency", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "RemovedDependency.dll")));
            Assert.Equal("new application", File.ReadAllText(Path.Combine(fixture.StagingDirectory, "TwitchArchivist.dll")));
            Assert.Equal("new dependency", File.ReadAllText(Path.Combine(fixture.StagingDirectory, "NewDependency.dll")));
            Assert.Equal("operator settings", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "appsettings.json")));
            Assert.Equal("database", File.ReadAllText(Path.Combine(fixture.PublishDirectory, "data", "twitcharchivist.db")));
        }
        finally
        {
            Directory.Delete(fixture.RootDirectory, recursive: true);
        }
    }

    private static StagedPublishFixture CreateStagedPublishFixture()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"TwitchArchivist-StagedPublish-{Guid.NewGuid():N}");
        var publishDirectory = Path.Combine(rootDirectory, "live");
        var stagingDirectory = Path.Combine(rootDirectory, "staging");
        var backupDirectory = Path.Combine(rootDirectory, "backup");

        Directory.CreateDirectory(publishDirectory);
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(Path.Combine(publishDirectory, "data"));
        Directory.CreateDirectory(Path.Combine(publishDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(publishDirectory, "tools"));
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "data"));

        File.WriteAllText(Path.Combine(publishDirectory, "TwitchArchivist.dll"), "old application");
        File.WriteAllText(Path.Combine(publishDirectory, "RemovedDependency.dll"), "removed dependency");
        File.WriteAllText(Path.Combine(publishDirectory, "appsettings.json"), "operator settings");
        File.WriteAllText(Path.Combine(publishDirectory, "data", "twitcharchivist.db"), "database");
        File.WriteAllText(Path.Combine(publishDirectory, "logs", "service.log"), "log");
        File.WriteAllText(Path.Combine(publishDirectory, "tools", "tool.exe"), "tool");

        File.WriteAllText(Path.Combine(stagingDirectory, "TwitchArchivist.dll"), "new application");
        File.WriteAllText(Path.Combine(stagingDirectory, "NewDependency.dll"), "new dependency");
        File.WriteAllText(Path.Combine(stagingDirectory, "appsettings.json"), "default settings");
        File.WriteAllText(Path.Combine(stagingDirectory, "data", "unexpected.db"), "unexpected database");

        return new StagedPublishFixture(rootDirectory, publishDirectory, stagingDirectory, backupDirectory);
    }

    private static CommandResult RunStagedPublishScript(StagedPublishFixture fixture, string commands)
    {
        var commonScriptPath = Path.Combine(RepositoryRoot, "scripts", "Service.Common.ps1");
        var testScriptPath = Path.Combine(fixture.RootDirectory, "test-staged-publish.ps1");
        File.WriteAllText(
            testScriptPath,
            $$"""
            $ErrorActionPreference = "Stop"
            . "{{commonScriptPath}}"
            $stagingDirectory = "{{fixture.StagingDirectory}}"
            $publishDirectory = "{{fixture.PublishDirectory}}"
            $backupDirectory = "{{fixture.BackupDirectory}}"
            {{commands}}
            """);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{testScriptPath}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        process!.WaitForExit();

        return new CommandResult(process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
    }

    private sealed record StagedPublishFixture(
        string RootDirectory,
        string PublishDirectory,
        string StagingDirectory,
        string BackupDirectory);

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
