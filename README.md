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

## Twitch developer setup

This app needs a Twitch application registration because it uses Twitch OAuth plus Helix/EventSub APIs. The values you need map directly to the `Twitch` section in `appsettings.json`:

- `Twitch:ClientId`
- `Twitch:ClientSecret`

Use `src/TwitchArchivist/appsettings.example.json` as the template for your local `appsettings.json` or `appsettings.Development.json`.

### Create a Twitch application

1. Sign in to the Twitch developer console at `https://dev.twitch.tv/console`.
2. Make sure the Twitch account you are using has email verification completed and two-factor authentication enabled. Twitch requires both before application registration is available.
3. Open `Applications`, then choose `Register Your Application`.
4. Enter an application name. Twitch requires the name to be unique across developer applications.
5. Add this OAuth redirect URL for local development:
   `http://localhost:5000/auth/twitch/callback`
6. Choose a category that best fits the app. For this project, a website or application-oriented category is the sensible choice.
7. Create the application, then open its `Manage` page.
8. Copy the `Client ID` into `Twitch:ClientId`.
9. Generate a `Client Secret` and copy it immediately into `Twitch:ClientSecret`.

Important operational details:

- Treat the client secret like a password. Do not commit it, paste it into issues, or ship it in a release artifact.
- Generating a new client secret invalidates the old one. If you rotate it in Twitch, update the local service configuration before restarting the app.
- This repo’s OAuth callback path is hosted by the same ASP.NET Core app as the diagnostics UI, so the redirect URI must match the running host and port exactly.

### Configure the local appsettings file

Start from the example file and set at least:

```json
"Twitch": {
  "ClientId": "your-client-id",
  "ClientSecret": "your-client-secret"
}
```

For local development, the callback URL in Twitch should stay aligned with the default app URL used by this repo:

- `http://localhost:5000/auth/twitch/callback`

After the app is running, start the user authorization flow from:

- `http://localhost:5000/auth/twitch/start`

The app will use the configured client ID and client secret to manage app access tokens, and it will store the resulting Twitch user authorization after you complete the browser flow from the diagnostics page.

## TwitchDownloaderCLI

`TwitchArchivist` shells out to `TwitchDownloaderCLI` after it identifies a completed VOD. This repo does not vendor the downloader binary; you install it separately and point the app at it.

### What it is

`TwitchDownloaderCLI` is the command-line edition of the upstream `lay295/TwitchDownloader` project. Upstream documents it as a Twitch VOD, clip, and chat downloader/renderer. For this repo, the relevant part is the CLI executable that can download VOD output on Windows.

### Install on Windows

1. Open the upstream releases page for `lay295/TwitchDownloader`.
2. Download the latest Windows release archive.
3. Extract `TwitchDownloaderCLI.exe` to a stable location.
4. If you also need FFmpeg, upstream documents a built-in helper:
   `TwitchDownloaderCLI.exe ffmpeg --download`

This repo defaults to looking here when `Downloader:ExecutablePath` is blank:

- `%APPDATA%\TwitchDownloaderCLI\TwitchDownloaderCLI.exe`

If you install it somewhere else, set:

```json
"Downloader": {
  "ExecutablePath": "C:\\full\\path\\to\\TwitchDownloaderCLI.exe"
}
```

### Verify the downloader installation

Before wiring the service to a real archive path, verify that Windows can launch the executable:

```powershell
& "C:\full\path\to\TwitchDownloaderCLI.exe" --help
```

If you rely on the default path, a reasonable manual layout is:

```text
%APPDATA%\TwitchDownloaderCLI\TwitchDownloaderCLI.exe
```

### Notes and constraints

- Keep the downloader executable outside the git repo and outside any path that is routinely cleaned by builds.
- If you replace the binary with a newer upstream release, no code change is required unless the CLI behavior changes incompatibly.
- FFmpeg may be needed depending on which TwitchDownloaderCLI operations you use. Upstream documents both standalone FFmpeg installs and the built-in download helper.

## Local development

1. Restore dependencies with `dotnet restore TwitchArchivist.slnx`
2. Build with `dotnet build TwitchArchivist.slnx`
3. Run the web host with `dotnet run --project src/TwitchArchivist`
4. Open `http://localhost:5000/diagnostics` and use `Authorize Twitch user token` to complete the EventSub WebSocket OAuth flow.

## Twitch OAuth callback

EventSub WebSocket subscriptions in this app use a Twitch user access token. The OAuth callback is served by the same ASP.NET host as the admin UI.

Register this redirect URI in the Twitch developer console for local use:

- `http://localhost:5000/auth/twitch/callback`

After the service is running, start the flow from:

- `http://localhost:5000/auth/twitch/start`

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

## GitHub releases

GitHub Actions now creates a release on every push to `main`.

- Workflow: `.github/workflows/release.yml`
- Tag format: `v<major>.<minor>.<patch>`
- Current bootstrap behavior: if no prior `v*` tag exists, the first release is `v0.1.0`

Each release runs the test suite, publishes a `win-x64` Release build, and attaches a zipped application bundle to the GitHub release.

Environment variables:

- `APPDATA`
  Default install root source. On this machine that resolves to a path like `C:\Users\DesertIce\AppData\Roaming`.
- `TWITCHARCHIVIST_INSTALL_ROOT`
  Optional explicit override for the install/publish directory root.

The repo intentionally does not track live `appsettings.json` files anymore. Start from:

- `src/TwitchArchivist/appsettings.example.json`
- `src/TwitchArchivist/appsettings.Development.example.json`

If `Downloader:ExecutablePath` is left blank, the app falls back to:

- `%APPDATA%\\TwitchDownloaderCLI\\TwitchDownloaderCLI.exe`
