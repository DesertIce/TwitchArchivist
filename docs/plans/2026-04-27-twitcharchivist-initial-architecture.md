# TwitchArchivist Initial Architecture Plan

## Summary

Build `TwitchArchivist` as a new Git-backed .NET repository with a single-process ASP.NET Core application that runs as a Windows Service, hosts a localhost-only admin UI, maintains lightweight SQLite state, listens to Twitch EventSub over WebSockets, and queues archive jobs when `stream.offline` is received.

Repository bootstrap is part of the plan:
- Initialize Git for change tracking.
- Add standard .NET ignores and repo scaffolding.
- Create one baseline initial commit before feature work starts.

For the Twitch integration, use `TwitchLib.Api` plus `TwitchLib.EventSub.Websockets` behind internal interfaces rather than wiring the library directly through the app. That is the best current fit for this repo because Twitch’s current EventSub docs support WebSockets, `stream.online` and `stream.offline` require no user authorization, and TwitchLib’s EventSub WebSocket package is current on NuGet and already designed around hosted-service usage. We should still isolate it because the package is pre-`1.0` and some public docs lag the latest package version.

The first version’s behavior is intentionally narrow:
- Monitor configured Twitch channels.
- On `stream.online`, record stream state only.
- On `stream.offline`, resolve the latest VOD with Helix, retry briefly until the VOD exists, then invoke a configured `TwitchDownloaderCLI` binary to save the video into that channel’s configured folder.
- Expose a small local admin UI for channel-directory mappings, service health, and recent jobs.

## Chosen Defaults

- Runtime target: `net8.0` for LTS stability on a Windows service.
- Hosting model: ASP.NET Core Generic Host with `UseWindowsService()`.
- Admin UI scope: localhost only.
- Twitch transport: EventSub WebSockets, not webhooks.
- Twitch auth model: app access token only for `stream.online` and `stream.offline`.
- Archive trigger: `stream.offline`.
- Archive output: video only.
- Downloader dependency: user-configured existing `TwitchDownloaderCLI` path.
- Persistence: SQLite for app configuration and operational state; Twitch client secret stays out of SQLite.
- Git bootstrap: `git init` plus `.gitignore` plus baseline initial commit.

## Recommended Stack

- App host: ASP.NET Core plus Generic Host
- Service integration: `Microsoft.Extensions.Hosting.WindowsServices`
- UI: Razor Pages with server-rendered forms
- Persistence: EF Core plus SQLite
- Twitch: `TwitchLib.Api`, `TwitchLib.EventSub.Websockets`
- Validation: `FluentValidation` or built-in model validation
- Logging: `Microsoft.Extensions.Logging`
- Process execution: `System.Diagnostics.Process`
- Time abstraction for tests: `TimeProvider`

## Repository Layout

- `src/TwitchArchivist/`
- `src/TwitchArchivist.Persistence/`
- `tests/TwitchArchivist.UnitTests/`
- `tests/TwitchArchivist.IntegrationTests/`
- `.gitignore`
- `README.md`
- `docs/plans/`

Keep v1 simple:
- One executable project for service plus web UI.
- One persistence library.
- One unit test project.
- One integration test project.

## Bootstrap Sequence

1. Run `git init` in `D:\code\TwitchArchivist`.
2. Create a .NET-focused `.gitignore` plus ignores for SQLite runtime files, logs, publish output, and local secrets.
3. Create the solution and project skeleton.
4. Add a minimal `README.md` describing purpose, local run mode, Windows service goal, and external prerequisites.
5. Add the initial architecture plan under `docs/plans/`.
6. Stage scaffolded files and create one baseline initial commit.

## Source References

- Twitch EventSub overview: https://dev.twitch.tv/docs/eventsub/
- Twitch EventSub subscription types: https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/
- Twitch EventSub WebSocket messages: https://dev.twitch.tv/docs/eventsub/websocket-reference/
- TwitchLib EventSub WebSockets repo: https://github.com/TwitchLib/TwitchLib.EventSub.Websockets
- TwitchLib.EventSub.Websockets NuGet: https://www.nuget.org/packages/TwitchLib.EventSub.Websockets
- TwitchLib.Api NuGet: https://www.nuget.org/packages/TwitchLib.Api
- TwitchDownloader repo: https://github.com/lay295/TwitchDownloader
