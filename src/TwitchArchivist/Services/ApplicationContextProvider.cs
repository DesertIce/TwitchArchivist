using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TwitchArchivist.Services;

public sealed class ApplicationContextProvider(IHostEnvironment environment, TimeProvider timeProvider)
{
    private readonly ApplicationContextSnapshot snapshot = CreateSnapshot(environment, timeProvider.GetUtcNow());

    public ApplicationContextSnapshot Snapshot => snapshot;

    private static ApplicationContextSnapshot CreateSnapshot(IHostEnvironment environment, DateTimeOffset startedUtc)
    {
        var assembly = typeof(Program).Assembly;
        var assemblyName = assembly.GetName();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var targetFramework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        var sourceRevisionId = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "SourceRevisionId", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return new ApplicationContextSnapshot(
            new ApplicationBuildInfo(
                assemblyName.Name ?? environment.ApplicationName,
                assemblyName.Version?.ToString(),
                informationalVersion ?? assemblyName.Version?.ToString() ?? "unknown",
                fileVersion,
                targetFramework,
                sourceRevisionId,
                assembly.Location),
            new ApplicationRuntimeContext(
                environment.ApplicationName,
                environment.EnvironmentName,
                environment.ContentRootPath,
                AppContext.BaseDirectory,
                Environment.CurrentDirectory,
                Environment.MachineName,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessId,
                startedUtc));
    }
}

public sealed record ApplicationContextSnapshot(
    ApplicationBuildInfo Build,
    ApplicationRuntimeContext RuntimeContext);

public sealed record ApplicationBuildInfo(
    string AssemblyName,
    string? AssemblyVersion,
    string InformationalVersion,
    string? FileVersion,
    string? TargetFramework,
    string? SourceRevisionId,
    string? AssemblyLocation);

public sealed record ApplicationRuntimeContext(
    string ApplicationName,
    string EnvironmentName,
    string ContentRootPath,
    string BaseDirectory,
    string CurrentDirectory,
    string MachineName,
    string OsDescription,
    string ProcessArchitecture,
    string FrameworkDescription,
    int ProcessId,
    DateTimeOffset StartedUtc);
