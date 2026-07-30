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

$serviceWasRunning = $null -ne $existingService -and
    $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped
$stagingDirectory = $null
$backupDirectory = $null
$promotedItemNames = @()
$promotionCompleted = $false
$deploymentRestored = $true
$updateSucceeded = $false

try {
    if (Test-IsGitCheckout) {
        $publishParentDirectory = Split-Path -Parent $resolvedPublishDirectory
        $publishDirectoryName = Split-Path -Leaf $resolvedPublishDirectory
        $operationId = [System.Guid]::NewGuid().ToString("N")
        $stagingDirectory = Join-Path $publishParentDirectory ".$publishDirectoryName.staging-$operationId"
        $backupDirectory = Join-Path $publishParentDirectory ".$publishDirectoryName.backup-$operationId"

        Invoke-DotNetPublishWithShouldProcess `
            -ProjectPath $resolvedProjectPath `
            -PublishDirectory $stagingDirectory `
            -Configuration $Configuration `
            -Cmdlet $PSCmdlet
    }

    if ($serviceWasRunning -and $PSCmdlet.ShouldProcess($ServiceName, "Stop Windows service")) {
        Assert-Administrator
        Stop-Service -Name $ServiceName -Force
        Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Stopped"
    }

    if (-not [string]::IsNullOrWhiteSpace($stagingDirectory) -and
        (Test-Path -LiteralPath $stagingDirectory) -and
        $PSCmdlet.ShouldProcess($resolvedPublishDirectory, "Promote staged service binaries")) {
        $promotedItemNames = @(Get-PublishItemNames `
            -StagingDirectory $stagingDirectory `
            -PublishDirectory $resolvedPublishDirectory)
        Move-StagedPublishIntoPlace `
            -StagingDirectory $stagingDirectory `
            -PublishDirectory $resolvedPublishDirectory `
            -BackupDirectory $backupDirectory `
            -ItemNames $promotedItemNames
        $promotionCompleted = $true
    }

    if (-not $NoStart -and $PSCmdlet.ShouldProcess($ServiceName, "Start Windows service")) {
        Assert-Administrator
        Start-Service -Name $ServiceName
        Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Running"
    }

    $updateSucceeded = $true
}
catch {
    $updateError = $_

    if ($promotionCompleted) {
        try {
            $currentService = Get-ServiceRecord -ServiceName $ServiceName
            if ($null -ne $currentService -and
                $currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                Stop-Service -Name $ServiceName -Force
                Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Stopped"
            }

            Restore-PublishBackup `
                -StagingDirectory $stagingDirectory `
                -PublishDirectory $resolvedPublishDirectory `
                -BackupDirectory $backupDirectory `
                -ItemNames $promotedItemNames
            $promotionCompleted = $false
        }
        catch {
            $deploymentRestored = $false
            Write-Warning "Failed to restore the previous service deployment: $($_.Exception.Message)"
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($backupDirectory) -and
        (Test-Path -LiteralPath $backupDirectory)) {
        try {
            if ($null -ne (Get-ChildItem -LiteralPath $backupDirectory -Force | Select-Object -First 1)) {
                $deploymentRestored = $false
                Write-Warning "The staged promotion rollback was incomplete; the backup was retained at '$backupDirectory'."
            }
        }
        catch {
            $deploymentRestored = $false
            Write-Warning "Could not verify the failed promotion backup at '$backupDirectory': $($_.Exception.Message)"
        }
    }

    if ($serviceWasRunning -and $deploymentRestored) {
        try {
            $currentService = Get-ServiceRecord -ServiceName $ServiceName
            if ($null -ne $currentService -and
                $currentService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                Start-Service -Name $ServiceName
                Wait-ForServiceStatus -ServiceName $ServiceName -DesiredStatus "Running"
            }
        }
        catch {
            Write-Warning "Failed to restart the previous service deployment: $($_.Exception.Message)"
        }
    }

    throw $updateError
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($stagingDirectory) -and
        (Test-Path -LiteralPath $stagingDirectory)) {
        try {
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
        }
        catch {
            Write-Warning "Failed to remove the staging directory '$stagingDirectory': $($_.Exception.Message)"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($backupDirectory) -and
        (Test-Path -LiteralPath $backupDirectory)) {
        try {
            $backupHasItems = $null -ne (Get-ChildItem -LiteralPath $backupDirectory -Force | Select-Object -First 1)
            if ($updateSucceeded -or -not $backupHasItems) {
                Remove-Item -LiteralPath $backupDirectory -Recurse -Force
            }
        }
        catch {
            Write-Warning "Failed to remove the backup directory '$backupDirectory': $($_.Exception.Message)"
        }
    }
}
