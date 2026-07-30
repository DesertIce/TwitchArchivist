[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RepositoryRoot {
    return Split-Path -Parent $PSScriptRoot
}

function Test-IsGitCheckout {
    $repoRoot = Get-RepositoryRoot
    return Test-Path -LiteralPath (Join-Path $repoRoot ".git")
}

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$BasePath
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Get-DefaultProjectPath {
    $repoRoot = Get-RepositoryRoot
    return Join-Path $repoRoot "src\TwitchArchivist\TwitchArchivist.csproj"
}

function Get-DefaultPublishDirectory {
    if (-not (Test-IsGitCheckout)) {
        return Get-RepositoryRoot
    }

    $installRoot = $env:TWITCHARCHIVIST_INSTALL_ROOT
    if ([string]::IsNullOrWhiteSpace($installRoot)) {
        if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
            throw "APPDATA is not set and TWITCHARCHIVIST_INSTALL_ROOT was not provided."
        }

        $installRoot = Join-Path $env:APPDATA "TwitchArchivist"
    }

    return [System.IO.Path]::GetFullPath($installRoot)
}

function Get-ServiceExecutablePath {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$PublishDirectory
    )

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    return Join-Path $PublishDirectory "$projectName.exe"
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Administrator {
    if (-not (Test-IsAdministrator)) {
        throw "This script must be run from an elevated PowerShell session."
    }
}

function Backup-AppSettingsFiles {
    param(
        [Parameter(Mandatory)]
        [string]$PublishDirectory
    )

    if (-not (Test-Path -LiteralPath $PublishDirectory)) {
        return $null
    }

    $settingsFiles = @(
        "appsettings.json",
        "appsettings.Development.json",
        "appsettings.Local.json",
        "appsettings.Development.Local.json"
    )

    $existingFiles = @($settingsFiles | Where-Object {
        Test-Path -LiteralPath (Join-Path $PublishDirectory $_)
    })

    if ($existingFiles.Count -eq 0) {
        return $null
    }

    $backupDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

    foreach ($file in $existingFiles) {
        Copy-Item -LiteralPath (Join-Path $PublishDirectory $file) -Destination (Join-Path $backupDirectory $file)
    }

    return $backupDirectory
}

function Restore-AppSettingsFiles {
    param(
        [string]$BackupDirectory,

        [Parameter(Mandatory)]
        [string]$PublishDirectory
    )

    if ([string]::IsNullOrWhiteSpace($BackupDirectory) -or -not (Test-Path -LiteralPath $BackupDirectory)) {
        return
    }

    Get-ChildItem -LiteralPath $BackupDirectory -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $PublishDirectory $_.Name) -Force
    }

    Remove-Item -LiteralPath $BackupDirectory -Recurse -Force
}

function Get-PublishItemNames {
    param(
        [Parameter(Mandatory)]
        [string]$StagingDirectory,

        [Parameter(Mandatory)]
        [string]$PublishDirectory
    )

    if (-not (Test-Path -LiteralPath $StagingDirectory)) {
        throw "Staging directory does not exist: $StagingDirectory"
    }

    return @(
        @($StagingDirectory, $PublishDirectory) |
            Where-Object { Test-Path -LiteralPath $_ } |
            ForEach-Object { Get-ChildItem -LiteralPath $_ -Force } |
            Where-Object {
                $_.Name -notlike "appsettings*.json" -and
                $_.Name -notin @("data", "logs", "tools")
            } |
            Select-Object -ExpandProperty Name |
            Sort-Object -Unique
    )
}

function Restore-PublishBackup {
    param(
        [Parameter(Mandatory)]
        [string]$StagingDirectory,

        [Parameter(Mandatory)]
        [string]$PublishDirectory,

        [Parameter(Mandatory)]
        [string]$BackupDirectory,

        [Parameter(Mandatory)]
        [string[]]$ItemNames
    )

    for ($index = $ItemNames.Count - 1; $index -ge 0; $index--) {
        $itemName = $ItemNames[$index]
        $stagedPath = Join-Path $StagingDirectory $itemName
        $publishedPath = Join-Path $PublishDirectory $itemName
        $backupPath = Join-Path $BackupDirectory $itemName

        if (Test-Path -LiteralPath $publishedPath) {
            if (Test-Path -LiteralPath $stagedPath) {
                throw "Cannot roll back '$itemName' because the staging path already exists."
            }

            Move-Item -LiteralPath $publishedPath -Destination $stagedPath
        }

        if (Test-Path -LiteralPath $backupPath) {
            Move-Item -LiteralPath $backupPath -Destination $publishedPath
        }
    }
}

function Move-StagedPublishIntoPlace {
    param(
        [Parameter(Mandatory)]
        [string]$StagingDirectory,

        [Parameter(Mandatory)]
        [string]$PublishDirectory,

        [Parameter(Mandatory)]
        [string]$BackupDirectory,

        [Parameter(Mandatory)]
        [string[]]$ItemNames
    )

    $resolvedStagingDirectory = [System.IO.Path]::GetFullPath($StagingDirectory)
    $resolvedPublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
    $resolvedBackupDirectory = [System.IO.Path]::GetFullPath($BackupDirectory)
    if ($resolvedStagingDirectory -eq $resolvedPublishDirectory -or
        $resolvedStagingDirectory -eq $resolvedBackupDirectory -or
        $resolvedPublishDirectory -eq $resolvedBackupDirectory) {
        throw "Staging, publish, and backup directories must be distinct."
    }

    if (-not (Test-Path -LiteralPath $resolvedStagingDirectory)) {
        throw "Staging directory does not exist: $resolvedStagingDirectory"
    }

    if (Test-Path -LiteralPath $resolvedBackupDirectory) {
        throw "Backup directory already exists: $resolvedBackupDirectory"
    }

    if (-not (Test-Path -LiteralPath $resolvedPublishDirectory)) {
        New-Item -ItemType Directory -Path $resolvedPublishDirectory -Force | Out-Null
    }

    New-Item -ItemType Directory -Path $resolvedBackupDirectory | Out-Null
    $preparedItemNames = [System.Collections.Generic.List[string]]::new()

    try {
        foreach ($itemName in $ItemNames) {
            $stagedPath = Join-Path $resolvedStagingDirectory $itemName
            $publishedPath = Join-Path $resolvedPublishDirectory $itemName
            $backupPath = Join-Path $resolvedBackupDirectory $itemName

            $stagedItemExists = Test-Path -LiteralPath $stagedPath
            $publishedItemExists = Test-Path -LiteralPath $publishedPath
            if (-not $stagedItemExists -and -not $publishedItemExists) {
                throw "Publish item does not exist in staging or the live directory: $itemName"
            }

            if ($publishedItemExists) {
                Move-Item -LiteralPath $publishedPath -Destination $backupPath
            }

            $preparedItemNames.Add($itemName)
            if ($stagedItemExists) {
                Move-Item -LiteralPath $stagedPath -Destination $publishedPath
            }
        }
    }
    catch {
        Restore-PublishBackup `
            -StagingDirectory $resolvedStagingDirectory `
            -PublishDirectory $resolvedPublishDirectory `
            -BackupDirectory $resolvedBackupDirectory `
            -ItemNames $preparedItemNames.ToArray()
        throw
    }
}

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$PublishDirectory,

        [Parameter(Mandatory)]
        [string]$Configuration
    )

    $projectDirectory = Split-Path -Parent $ProjectPath
    $backupDirectory = Backup-AppSettingsFiles -PublishDirectory $PublishDirectory

    try {
        if (-not (Test-Path -LiteralPath $PublishDirectory)) {
            New-Item -ItemType Directory -Path $PublishDirectory -Force | Out-Null
        }

        & dotnet publish $ProjectPath -c $Configuration -o $PublishDirectory -r win-x64 --self-contained true
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Restore-AppSettingsFiles -BackupDirectory $backupDirectory -PublishDirectory $PublishDirectory
    }
}

function Invoke-DotNetPublishWithShouldProcess {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$PublishDirectory,

        [Parameter(Mandatory)]
        [string]$Configuration,

        [Parameter(Mandatory)]
        [System.Management.Automation.PSCmdlet]$Cmdlet
    )

    if (-not (Test-IsGitCheckout)) {
        return
    }

    if ($Cmdlet.ShouldProcess($PublishDirectory, "Publish service binaries from $ProjectPath")) {
        Invoke-DotNetPublish -ProjectPath $ProjectPath -PublishDirectory $PublishDirectory -Configuration $Configuration
    }
}

function Get-ServiceRecord {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName
    )

    return Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Wait-ForServiceStatus {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName,

        [Parameter(Mandatory)]
        [string]$DesiredStatus,

        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $service = Get-ServiceRecord -ServiceName $ServiceName
        if ($null -eq $service) {
            if ($DesiredStatus -eq "Deleted") {
                return
            }
        }
        elseif ($service.Status.ToString() -eq $DesiredStatus) {
            return
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for service '$ServiceName' to reach status '$DesiredStatus'."
}

function Set-ServiceDescription {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName,

        [Parameter(Mandatory)]
        [string]$Description
    )

    & sc.exe description $ServiceName $Description | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set service description for '$ServiceName'."
    }
}

function Remove-ServiceRegistration {
    param(
        [Parameter(Mandatory)]
        [string]$ServiceName
    )

    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to delete service '$ServiceName'."
    }
}
