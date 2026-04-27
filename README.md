# TwitchArchivist

TwitchArchivist is a Windows-service-oriented ASP.NET Core application that will listen for Twitch EventSub messages, map channels to local archive directories, and invoke `TwitchDownloaderCLI` when a stream ends.

## Planned scope

- Run as a Windows Service in production and as a console app in development.
- Host a lightweight localhost-only web UI for configuration and diagnostics.
- Store operational configuration in SQLite.
- Use Twitch EventSub WebSockets plus Helix lookups to discover completed VODs.
- Invoke an existing `TwitchDownloaderCLI` installation to download video output.

## Prerequisites

- .NET 8 SDK
- Git
- A local `TwitchDownloaderCLI` installation for later implementation stages

## Local development

1. Restore dependencies with `dotnet restore TwitchArchivist.slnx`
2. Build with `dotnet build TwitchArchivist.slnx`
3. Run the web host with `dotnet run --project src/TwitchArchivist`

## Windows service scripts

The repo includes publish-first PowerShell scripts under `scripts/`:

- `scripts/install-service.ps1`
- `scripts/update-service.ps1`
- `scripts/uninstall-service.ps1`

Each script supports `-WhatIf` for dry-run verification.

Typical install:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1
```

Typical update:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\update-service.ps1
```

Typical uninstall:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-service.ps1 -RemovePublishDirectory
```

Defaults:

- Service name: `TwitchArchivist`
- Publish directory: `%APPDATA%\TwitchArchivist`
- Build configuration: `Release`

The install and update scripts publish the app before touching the service and preserve any existing `appsettings*.json` files in the publish directory so local operator config is not overwritten during redeploys.

Environment variables:

- `APPDATA`
  Default install root source. On this machine that resolves to a path like `C:\Users\DesertIce\AppData\Roaming`.
- `TWITCHARCHIVIST_INSTALL_ROOT`
  Optional explicit override for the install/publish directory root.

The repo intentionally does not track live `appsettings.json` files anymore. Start from:

- `src/TwitchArchivist/appsettings.example.json`
- `src/TwitchArchivist/appsettings.Development.example.json`
