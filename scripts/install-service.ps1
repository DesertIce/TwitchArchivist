[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = "TwitchArchivist",
    [string]$ProjectPath = "src/TwitchArchivist/TwitchArchivist.csproj",
    [string]$PublishDirectory = "",
    [string]$Configuration = "Release",
    [string]$ServiceDescription = "Twitch stream archiver service",
    [switch]$NoStart
)

. (Join-Path $PSScriptRoot "Service.Common.ps1")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Get-RepositoryRoot
$isGitCheckout = Test-IsGitCheckout
$resolvedProjectPath = Resolve-AbsolutePath -Path $ProjectPath -BasePath $repoRoot
$resolvedPublishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Get-DefaultPublishDirectory
}
else {
    Resolve-AbsolutePath -Path $PublishDirectory -BasePath $repoRoot
}
$serviceExecutablePath = Get-ServiceExecutablePath -ProjectPath $resolvedProjectPath -PublishDirectory $resolvedPublishDirectory

if ($isGitCheckout -and -not (Test-Path -LiteralPath $resolvedProjectPath) -and -not $WhatIfPreference) {
    throw "Project file was not found: $resolvedProjectPath"
}

$existingService = Get-ServiceRecord -ServiceName $ServiceName
if ($null -ne $existingService -and -not $WhatIfPreference) {
    throw "Service '$ServiceName' already exists. Use update-service.ps1 to redeploy it."
}

Invoke-DotNetPublishWithShouldProcess -ProjectPath $resolvedProjectPath -PublishDirectory $resolvedPublishDirectory -Configuration $Configuration -Cmdlet $PSCmdlet

if ($PSCmdlet.ShouldProcess($ServiceName, "Create Windows service")) {
    Assert-Administrator

    New-Service -Name $ServiceName -BinaryPathName $serviceExecutablePath -DisplayName $ServiceName -StartupType Automatic | Out-Null
    Set-ServiceDescription -ServiceName $ServiceName -Description $ServiceDescription

    if (-not $NoStart) {
        Start-Service -Name $ServiceName
        Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Running"
    }
}
