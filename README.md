# TwitchArchivist

TwitchArchivist is an ASP.NET Core 8 application for Windows that watches Twitch channels through EventSub, tracks archive jobs in SQLite, and downloads completed VODs with `TwitchDownloaderCLI`.

It is designed to run as a Windows Service in production and as a normal ASP.NET Core app during development. The built-in web UI is the local operator surface for channel mappings, diagnostics, logs, and recent archive jobs. When installed as a Windows Service with the stock scripts and config, the UI is at **`http://localhost:5000`** by default; local `dotnet run` uses different ports from `launchSettings.json` (see [Ports and listen URLs](#ports-and-listen-urls)).

## Current capabilities

- Listen for Twitch `stream.online` and `stream.offline` events.
- Track channel-to-directory mappings in SQLite.
- Queue archive jobs when a stream ends, then wait for the corresponding VOD to become discoverable.
- Download VODs by invoking an existing `TwitchDownloaderCLI.exe` installation.
- Optionally auto-prune older successful VOD files per channel after a new archive completes.
- Expose a localhost web UI plus JSON runtime endpoints for diagnostics and health checks.
- Run with direct EventSub WebSocket transport or conduit-backed WebSocket transport.

## Requirements

- Windows
- .NET 8 SDK
- A Twitch developer application with a client ID and client secret
- A local `TwitchDownloaderCLI` installation

## Repository layout

- `src/TwitchArchivist`: ASP.NET Core host, Razor Pages UI, runtime services
- `src/TwitchArchivist.Persistence`: EF Core persistence layer and migrations
- `tests`: unit and integration tests
- `scripts`: Windows service install, update, and uninstall scripts

## Configuration

Start from one of the example files:

- `src/TwitchArchivist/appsettings.example.json`
- `src/TwitchArchivist/appsettings.Development.example.json`

Key sections:

```json
{
  "Storage": {
    "DatabasePath": "data/twitcharchivist.db"
  },
  "Downloader": {
    "ExecutablePath": ""
  },
  "Twitch": {
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "EventSubTransportMode": "conduit-websocket"
  },
  "FileLogging": {
    "DirectoryPath": "logs",
    "FilePrefix": "twitcharchivist",
    "RetainedDayCount": 3
  }
}
```

Notes:

- `Storage:DatabasePath` is relative to the app content root unless you provide an absolute path.
- `Downloader:ExecutablePath` can be left blank to use the fallback path `%APPDATA%\TwitchDownloaderCLI\TwitchDownloaderCLI.exe`.
- The checked-in example files currently opt into `conduit-websocket`.
- The options class default is still `websocket` if `EventSubTransportMode` is omitted entirely.

## Twitch developer setup

This app uses both Twitch OAuth and Helix/EventSub APIs. You need a Twitch application registration and must populate:

- `Twitch:ClientId`
- `Twitch:ClientSecret`

Create the Twitch app in the developer console at `https://dev.twitch.tv/console`, then register an OAuth redirect URI that matches the host and port where TwitchArchivist is actually running.

The diagnostics page now walks through this setup, shows the current callback URL for the running host, and can save `Twitch:ClientId` and `Twitch:ClientSecret` directly into `appsettings.json`.

Examples:

- Installed Windows Service (default listen URL; see [Ports and listen URLs](#ports-and-listen-urls)): `http://localhost:5000/auth/twitch/callback`
- Development HTTP profile from `launchSettings.json`: `http://localhost:5222/auth/twitch/callback`
- Development HTTPS profile from `launchSettings.json`: `https://localhost:7153/auth/twitch/callback`
- If you override URLs or run behind a different port, use that exact callback instead

Important:

- The redirect URI must match the running host, scheme, and port exactly.
- Generating a new Twitch client secret invalidates the old one.
- Do not commit secrets to the repo.

Once the app is running, start user authorization from:

- `/auth/twitch/start`

The callback is served by the same ASP.NET Core host:

- `/auth/twitch/callback`

The resulting Twitch user token is stored in the SQLite-backed application state.

## TwitchDownloaderCLI

The diagnostics page now supports one-click managed setup for `TwitchDownloaderCLI`. It downloads the latest Windows x64 CLI release from the upstream [`lay295/TwitchDownloader`](https://github.com/lay295/TwitchDownloader) GitHub releases page, extracts it into the app directory, and saves the resolved executable path into `appsettings.json`.

Managed install location:

- `<app-root>\tools\TwitchDownloaderCLI\current\TwitchDownloaderCLI.exe`

Managed install metadata:

- `<app-root>\tools\TwitchDownloaderCLI\managed-install.json`

If you prefer to manage the binary yourself, the app still supports a manual path.

Expected setting:

- `Downloader:ExecutablePath`

Fallback when blank:

- `%APPDATA%\TwitchDownloaderCLI\TwitchDownloaderCLI.exe`

Typical explicit configuration:

```json
{
  "Downloader": {
    "ExecutablePath": "C:\\Tools\\TwitchDownloaderCLI\\TwitchDownloaderCLI.exe"
  }
}
```

The app launches the downloader like this:

```text
TwitchDownloaderCLI.exe videodownload --id <vodId> -o <outputPath>
```

Manual verification:

```powershell
& "C:\Tools\TwitchDownloaderCLI\TwitchDownloaderCLI.exe" --version
```

The diagnostics page can either save a manual executable path or install the managed copy and validates the selected executable by running `--version` and checking for a `TwitchDownloaderCLI ...` banner.

## Local development

1. Copy `src/TwitchArchivist/appsettings.example.json` or `src/TwitchArchivist/appsettings.Development.example.json` into a local `appsettings.json` or `appsettings.Development.json`.
2. Populate `Twitch:ClientId` and `Twitch:ClientSecret`.
3. Restore packages:

```powershell
dotnet restore TwitchArchivist.slnx
```

4. Build:

```powershell
dotnet build TwitchArchivist.slnx
```

5. Run the web app:

```powershell
dotnet run --project src/TwitchArchivist
```

By default, local development uses the `launchSettings.json` profiles:

- HTTP: `http://localhost:5222`
- HTTPS: `https://localhost:7153`

For how this differs from an installed Windows Service (including the default port), see [Ports and listen URLs](#ports-and-listen-urls).

## Ports and listen URLs

Use this when wiring the Twitch OAuth redirect URI, bookmarks, or reverse proxies.

**Installed Windows Service** (`scripts/install-service.ps1`, published `TwitchArchivist.exe`):

- Listens on **`http://localhost:5000`** by default. The service scripts do not set `ASPNETCORE_URLS`, and the checked-in `appsettings` files do not define Kestrel URLs, so Kestrel uses the ASP.NET Core default when no URL configuration is present.
- To use another address or port, set **`ASPNETCORE_URLS`** (machine, user, or service environment) or otherwise configure application URLs per [ASP.NET Core URL configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints).

**Local development** (`dotnet run`, Visual Studio / Rider):

- Uses **`Properties/launchSettings.json`**, not the service defaults: HTTP **`5222`**, HTTPS **`7153`** (`http` / `https` profiles). IIS Express in that file uses **`5551`** for HTTP.

## Web UI

Primary pages:

- `/`: dashboard with service overview, recent jobs, downloader/EventSub/OAuth summary
- `/channels`: create, edit, enable, disable, and delete channel mappings
- `/jobs`: recent archive job history
- `/diagnostics`: downloader validation, EventSub status, OAuth status, diagnostics message
- `/logs`: recent in-memory and rolling-file log output

Channel mapping behavior:

- Each channel maps to one output directory.
- The UI supports Twitch login autocomplete via Helix search.
- Auto-prune can be enabled per channel.
- `AutoPruneVodCount` controls how many successful VOD files are retained after a new successful archive.

## Runtime and health endpoints

JSON endpoints:

- `/healthz`
- `/api/runtime-status`
- `/api/twitch/channels/search?query=<text>`
- `/api/filesystem/roots`
- `/api/filesystem/directories?path=<absolute-path>`
- `/api/filesystem/entries?path=<absolute-path>&includeFiles=true&searchPattern=*.exe`

`/healthz` reports the high-level service state, including:

- database readiness
- downloader validation state
- EventSub transport and connection state
- conduit id and shard counts
- Twitch user authorization state

`/api/runtime-status` includes the richer diagnostics payload used by the UI, including the last validation timestamps and recent conduit error fields.

## EventSub transport modes

Configure `Twitch:EventSubTransportMode` as one of:

- `websocket`
- `conduit-websocket`

Related conduit settings:

```json
"Twitch": {
  "EventSubTransportMode": "conduit-websocket",
  "EventSubConduitShardCount": 4,
  "EventSubConduitId": null,
  "EventSubConduitAssignmentTimeoutSeconds": 10,
  "EventSubConduitReconcileIntervalSeconds": 60
}
```

Operational notes:

- The example config files currently use `conduit-websocket`.
- If the setting is absent, the code falls back to direct `websocket`.
- Runtime status surfaces the active transport mode, conduit id, configured shards, active shards, disabled shards, last shard assignment error, last reconcile error, and last rate-limit timestamp.

## Archive job flow

When a tracked channel goes offline:

1. TwitchArchivist queues an archive job.
2. The worker waits for Twitch to expose the finished archive VOD.
3. It retries VOD discovery using the configured delay and retry count.
4. It downloads the matching VOD with `TwitchDownloaderCLI`.
5. If enabled for that channel, it prunes older successful files after the new archive succeeds.

Relevant `Twitch` settings:

- `VodDiscoveryInitialDelaySeconds`
- `VodDiscoveryRetryCount`
- `VodDiscoveryRetryDelaySeconds`

## Windows Service scripts

PowerShell helpers live under `scripts/`:

- `scripts/install-service.ps1`
- `scripts/update-service.ps1`
- `scripts/uninstall-service.ps1`

Examples:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\update-service.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-service.ps1 -RemovePublishDirectory
```

After install, open the web UI at the [default service URL](#ports-and-listen-urls) unless you override URLs.

Defaults:

- service name: `TwitchArchivist`
- project path: `src/TwitchArchivist/TwitchArchivist.csproj`
- publish directory: `%APPDATA%\TwitchArchivist`
- configuration: `Release`

Behavior:

- `install-service.ps1` publishes the app, creates the Windows service, and starts it unless `-NoStart` is used.
- `update-service.ps1` stops the service, republishes, and starts it again unless `-NoStart` is used.
- `uninstall-service.ps1` stops and deletes the service, and optionally removes the publish directory.
- All three scripts support `-WhatIf`.
- The install and update scripts preserve existing `appsettings*.json` files in the publish directory during republish.
- When run from an extracted bundle without a `.git` directory, the scripts skip `dotnet publish` and use the extracted directory as the publish root.

These scripts require an elevated PowerShell session when they create, start, stop, or delete the Windows service.

## Logging

The app writes:

- recent in-memory log entries for the `/logs` page
- rolling file logs under `FileLogging:DirectoryPath`

Default file logging settings:

```json
"FileLogging": {
  "DirectoryPath": "logs",
  "FilePrefix": "twitcharchivist",
  "RetainedDayCount": 3
}
```

## Releases

GitHub Actions creates a release on each push to `main`.

- Workflow: `.github/workflows/release.yml`
- Tests run before publish
- Release artifact: zipped `win-x64` publish output
- First bootstrap tag when no prior version exists: `v0.1.0`
- Subsequent releases increment the patch version from the latest `v*` tag

## Security and local config

- Do not commit live `appsettings.json` files with real secrets.
- Keep `TwitchDownloaderCLI.exe` outside the repo.
- Treat the Twitch client secret like a password.
- For service installs, ensure the configured downloader path and archive output directories are accessible to the Windows service account.
