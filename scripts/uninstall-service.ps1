[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = "TwitchArchivist",
    [string]$PublishDirectory = "",
    [switch]$RemovePublishDirectory
)

. (Join-Path $PSScriptRoot "Service.Common.ps1")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Get-RepositoryRoot
$resolvedPublishDirectory = if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Get-DefaultPublishDirectory
}
else {
    Resolve-AbsolutePath -Path $PublishDirectory -BasePath $repoRoot
}
$existingService = Get-ServiceRecord -ServiceName $ServiceName

if ($null -ne $existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Stop Windows service")) {
        Assert-Administrator
        Stop-Service -Name $ServiceName -Force
        Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Stopped"
    }
}

if ($null -ne $existingService) {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Delete Windows service")) {
        Assert-Administrator
        Remove-ServiceRegistration -ServiceName $ServiceName
        Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Deleted"
    }
}
elseif (-not $WhatIfPreference) {
    throw "Service '$ServiceName' does not exist."
}

if ($RemovePublishDirectory -and $PSCmdlet.ShouldProcess($resolvedPublishDirectory, "Remove published service directory")) {
    if (Test-Path -LiteralPath $resolvedPublishDirectory) {
        Remove-Item -LiteralPath $resolvedPublishDirectory -Recurse -Force
    }
}
