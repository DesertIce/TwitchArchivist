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

The repository currently contains only the baseline scaffold:

1. Restore dependencies with `dotnet restore TwitchArchivist.slnx`
2. Build with `dotnet build TwitchArchivist.slnx`
3. Run the web host with `dotnet run --project src/TwitchArchivist`

The host is structured so later iterations can add Windows Service hosting, SQLite persistence, Twitch integration, and downloader orchestration without restructuring the solution.
