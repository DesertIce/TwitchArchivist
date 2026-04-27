[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = "TwitchArchivist",
    [string]$ProjectPath = "src/TwitchArchivist/TwitchArchivist.csproj",
    [string]$PublishDirectory = "",
    [string]$Configuration = "Release",
    [switch]$NoStart
)

. (Join-Path $PSScriptRoot "Service.Common.ps1")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Get-RepositoryRoot
$resolvedProjectPath = Resolve-AbsolutePath -Path $ProjectPath -BasePath $repoRoot
$resolvedPublishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Get-DefaultPublishDirectory
}
else {
    Resolve-AbsolutePath -Path $PublishDirectory -BasePath $repoRoot
}

$existingService = Get-ServiceRecord -ServiceName $ServiceName
if ($null -eq $existingService -and -not $WhatIfPreference) {
    throw "Service '$ServiceName' does not exist. Use install-service.ps1 first."
}

if ($null -ne $existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Stop Windows service")) {
        Assert-Administrator
        Stop-Service -Name $ServiceName -Force
        Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Stopped"
    }
}

Invoke-DotNetPublishWithShouldProcess -ProjectPath $resolvedProjectPath -PublishDirectory $resolvedPublishDirectory -Configuration $Configuration -Cmdlet $PSCmdlet

if (-not $NoStart -and $PSCmdlet.ShouldProcess($ServiceName, "Start Windows service")) {
    Assert-Administrator
    Start-Service -Name $ServiceName
    Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Running"
}
