# EventSub Conduit Integration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Migrate TwitchArchivist from session-bound EventSub WebSocket subscriptions to conduit-backed WebSocket shards so the app can support large channel counts without recreating subscriptions on every reconnect.

**Architecture:** Introduce a conduit control plane that owns conduit lifecycle, shard assignment, and subscription reconciliation using Twitch app-access-token conduit APIs. Keep the existing event-processing semantics for `stream.online` and `stream.offline`, but route notifications through a shard-aware WebSocket layer that is decoupled from individual session IDs. Persist conduit, shard, and subscription state so reconnects and restarts repair transport assignment instead of recreating every subscription.

**Tech Stack:** ASP.NET Core 8, EF Core SQLite persistence, Twitch Helix/EventSub APIs, TwitchLib EventSub WebSockets for transport sessions, Polly for resilience and backoff, xUnit for unit/integration tests.

---

## Assumptions

- Target transport is **conduit + WebSocket shards**, not conduit + webhooks.
- A **single conduit** is sufficient initially; scale comes from increasing shard count rather than creating multiple conduits.
- Existing subscription types remain `stream.online` and `stream.offline`.
- Existing local deployment model remains a single Windows service process unless future work explicitly adds multi-process coordination.

## Success Criteria

- Restarting or reconnecting the app does **not** recreate the full subscription set.
- Conduit shards are reassigned to fresh WebSocket sessions within Twitch’s 10-second assignment window.
- Subscription reconciliation becomes conduit-id based instead of session-id based.
- Shard disablement, conduit drift, and rate-limit behavior are visible in logs and runtime diagnostics.
- The app can scale shard count without rewriting subscription ownership logic.

## External References

- Twitch EventSub overview: `https://dev.twitch.tv/docs/eventsub/`
- Twitch conduit flow and limits: `https://dev.twitch.tv/docs/eventsub/handling-conduit-events/`

### Task 1: Add Conduit Domain Models and Persistence

**Files:**
- Create: `src/TwitchArchivist.Persistence/Entities/EventSubConduit.cs`
- Create: `src/TwitchArchivist.Persistence/Entities/EventSubConduitShard.cs`
- Create: `src/TwitchArchivist.Persistence/Entities/EventSubSubscriptionBinding.cs`
- Modify: `src/TwitchArchivist.Persistence/TwitchArchivistDbContext.cs`
- Create: `src/TwitchArchivist.Persistence/Migrations/<timestamp>_AddEventSubConduitState.cs`
- Test: `tests/TwitchArchivist.UnitTests/Persistence/EventSubConduitPersistenceTests.cs`

**Step 1: Write the failing persistence test**

Cover:
- one conduit with many shards
- shard state includes shard id, assigned session id, status, last welcome time, last assignment time
- subscription binding is stored per channel + type + conduit id

**Step 2: Run test to verify it fails**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubConduitPersistenceTests`

Expected: FAIL because entities and model mappings do not exist.

**Step 3: Add minimal persistence model**

Implement:
- `EventSubConduit` for logical conduit identity and shard count
- `EventSubConduitShard` for shard transport assignment state
- `EventSubSubscriptionBinding` for conduit-scoped subscription state

Model requirements:
- unique conduit id
- unique `(ConduitId, ShardId)`
- unique `(ChannelConfigurationId, SubscriptionType)` if only one active binding is allowed

**Step 4: Add EF mappings and migration**

Add indexes and relationships. Avoid storing ephemeral transport state only in memory.

**Step 5: Run tests and migration verification**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubConduitPersistenceTests`
- `dotnet test TwitchArchivist.slnx`

**Step 6: Commit**

`git commit -m "feat: persist eventsub conduit state"`

### Task 2: Extend Twitch Options for Conduit Mode

**Files:**
- Modify: `src/TwitchArchivist/Models/TwitchOptions.cs`
- Modify: `src/TwitchArchivist/appsettings.json`
- Modify: `src/TwitchArchivist/appsettings.example.json`
- Modify: `src/TwitchArchivist/appsettings.Development.json`
- Modify: `src/TwitchArchivist/appsettings.Development.example.json`
- Test: `tests/TwitchArchivist.UnitTests/Services/Twitch/TwitchOptionsBindingTests.cs`

**Step 1: Write the failing configuration test**

Cover binding for:
- `EventSubTransportMode` (`websocket` or `conduit-websocket`)
- `EventSubConduitShardCount`
- `EventSubConduitId`
- `EventSubConduitAssignmentTimeoutSeconds`
- `EventSubConduitReconcileIntervalSeconds`

**Step 2: Run test to verify it fails**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter TwitchOptionsBindingTests`

**Step 3: Add minimal options**

Default strategy:
- current default remains direct websocket until migration is enabled
- conduit mode is opt-in behind config

**Step 4: Document config samples**

Add examples with conservative defaults and comments in example config files.

**Step 5: Run tests**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter TwitchOptionsBindingTests`

**Step 6: Commit**

`git commit -m "feat: add eventsub conduit configuration"`

### Task 3: Add Conduit API Support to TwitchHelixClient

**Files:**
- Modify: `src/TwitchArchivist/Services/Twitch/ITwitchHelixClient.cs`
- Modify: `src/TwitchArchivist/Services/Twitch/TwitchHelixClient.cs`
- Create: `src/TwitchArchivist/Services/Twitch/EventSubConduitRecord.cs`
- Create: `src/TwitchArchivist/Services/Twitch/EventSubConduitShardRecord.cs`
- Test: `tests/TwitchArchivist.UnitTests/Services/Twitch/TwitchApiClientTests.cs`

**Step 1: Write failing API client tests**

Add tests for:
- create conduit
- update conduit shard assignments
- update conduit shard count
- create conduit-backed subscription payload

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter TwitchApiClientTests`

**Step 3: Add Helix client methods**

Add app-token methods for:
- `CreateEventSubConduitAsync`
- `GetEventSubConduitsAsync` or direct fetch if needed
- `UpdateEventSubConduitAsync`
- `UpdateEventSubConduitShardsAsync`
- `CreateConduitSubscriptionAsync`

Keep direct websocket subscription methods temporarily for compatibility.

**Step 4: Add error surfaces**

Do not hide `429` or assignment failures. Include enough context in thrown exceptions to log conduit id, shard id, and type.

**Step 5: Run tests**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter TwitchApiClientTests`

**Step 6: Commit**

`git commit -m "feat: add helix conduit api support"`

### Task 4: Introduce Shard-Aware WebSocket Abstractions

**Files:**
- Create: `src/TwitchArchivist/Services/Twitch/IEventSubShardClient.cs`
- Create: `src/TwitchArchivist/Services/Twitch/TwitchLibEventSubShardClient.cs`
- Create: `src/TwitchArchivist/Services/Twitch/EventSubShardConnectionState.cs`
- Modify: `src/TwitchArchivist/Services/Twitch/IEventSubWebsocketClient.cs`
- Modify: `src/TwitchArchivist/Services/Twitch/TwitchLibEventSubWebsocketClientAdapter.cs`
- Test: `tests/TwitchArchivist.UnitTests/Services/Twitch/EventSubShardClientTests.cs`

**Step 1: Write failing shard-client tests**

Cover:
- multiple shard clients can exist concurrently
- each exposes its own session id and lifecycle events
- welcome/session events are capturable before assignment

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubShardClientTests`

**Step 3: Add shard-aware client abstraction**

Key design point:
- one conduit shard maps to one live websocket session
- do not reuse a singleton `IEventSubWebsocketClient` abstraction for all shards

**Step 4: Preserve event payload compatibility**

Keep `stream.online` and `stream.offline` notifications flowing through existing record types where possible.

**Step 5: Run tests**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubShardClientTests`

**Step 6: Commit**

`git commit -m "refactor: add shard-aware eventsub websocket client"`

### Task 5: Build a Conduit Control Plane Hosted Service

**Files:**
- Create: `src/TwitchArchivist/Services/Twitch/TwitchEventSubConduitHostedService.cs`
- Create: `src/TwitchArchivist/Services/Twitch/IEventSubConduitCoordinator.cs`
- Create: `src/TwitchArchivist/Services/Twitch/EventSubConduitCoordinator.cs`
- Modify: `src/TwitchArchivist/Program.cs`
- Test: `tests/TwitchArchivist.UnitTests/Services/Twitch/TwitchEventSubConduitHostedServiceTests.cs`

**Step 1: Write failing hosted-service tests**

Cover:
- creates conduit when missing
- scales shard count to configured size
- opens shard websocket sessions
- assigns transports inside the 10-second window
- persists shard assignment state

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter TwitchEventSubConduitHostedServiceTests`

**Step 3: Implement conduit startup flow**

Startup flow:
1. load or create conduit
2. ensure configured shard count
3. establish shard websocket sessions
4. PATCH conduit shard assignments using session ids
5. mark shards enabled only after successful assignment

**Step 4: Add recovery loop**

On disconnect or shard disablement:
- replace only the affected shard session
- reassign that shard
- avoid full resubscription

**Step 5: Run tests**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter TwitchEventSubConduitHostedServiceTests`

**Step 6: Commit**

`git commit -m "feat: add eventsub conduit control plane"`

### Task 6: Replace Session-Bound Subscription Reconciliation

**Files:**
- Modify: `src/TwitchArchivist/Services/Twitch/EventSubSubscriptionSynchronizer.cs`
- Modify: `src/TwitchArchivist/Services/Twitch/IEventSubSubscriptionSynchronizer.cs`
- Modify: `src/TwitchArchivist/Services/Twitch/TwitchEventSubHostedService.cs`
- Test: `tests/TwitchArchivist.UnitTests/Services/Twitch/EventSubSubscriptionSynchronizerTests.cs`

**Step 1: Write failing reconciliation tests**

Cover:
- existing conduit subscription is reused across shard reconnects
- reconnect does not recreate all subscriptions
- missing subscription creates a conduit-backed subscription
- disabled channel can mark binding stale or removable without deleting unrelated bindings

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubSubscriptionSynchronizerTests`

**Step 3: Change reconciliation key**

Move from:
- `subscription type + broadcaster + session id`

To:
- `subscription type + broadcaster + conduit id`

**Step 4: Remove direct create-on-reconnect behavior**

Reconnect should repair shard assignment, not recreate every subscription.

**Step 5: Run tests**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubSubscriptionSynchronizerTests`

**Step 6: Commit**

`git commit -m "refactor: reconcile eventsub subscriptions by conduit"`

### Task 7: Handle Conduit Failure and Cleanup Scenarios

**Files:**
- Create: `src/TwitchArchivist/Services/Twitch/EventSubConduitCleanupService.cs`
- Modify: `src/TwitchArchivist/Services/Twitch/TwitchEventSubConduitHostedService.cs`
- Modify: `src/TwitchArchivist/Services/Twitch/TwitchHelixClient.cs`
- Test: `tests/TwitchArchivist.UnitTests/Services/Twitch/EventSubConduitCleanupTests.cs`

**Step 1: Write failing cleanup tests**

Cover:
- stale shard session is replaced
- disabled shard triggers reassignment
- old direct-websocket subscriptions can be cleaned during migration
- rate-limit path backs off instead of re-flooding POSTs

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubConduitCleanupTests`

**Step 3: Implement cleanup strategy**

Required behaviors:
- react to `conduit.shard.disabled`
- periodically reconcile shard assignment health
- optionally delete obsolete session-bound subscriptions after successful cutover

**Step 4: Add `429`-aware resilience**

Use Polly or equivalent to:
- honor retry-after when available
- exponentially back off create/assign operations
- avoid concurrent subscription floods

**Step 5: Run tests**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter EventSubConduitCleanupTests`

**Step 6: Commit**

`git commit -m "feat: add conduit cleanup and rate-limit recovery"`

### Task 8: Extend Diagnostics and Runtime Status

**Files:**
- Modify: `src/TwitchArchivist/Services/RuntimeStatusStore.cs`
- Modify: `src/TwitchArchivist/Program.cs`
- Modify: `src/TwitchArchivist/Pages/Diagnostics/Index.cshtml.cs`
- Modify: `src/TwitchArchivist/Pages/Diagnostics/Index.cshtml`
- Test: `tests/TwitchArchivist.UnitTests/Pages/Diagnostics/IndexModelTests.cs`

**Step 1: Write failing diagnostics tests**

Cover runtime status fields for:
- transport mode
- conduit id
- configured shard count
- active shard count
- disabled shard count
- last shard assignment error
- last subscription reconcile error

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter IndexModelTests`

**Step 3: Add status surfaces**

Expose in `/healthz` and `/api/runtime-status`:
- conduit mode enabled
- conduit state summary
- last rate-limit timestamp

**Step 4: Run tests**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj --filter IndexModelTests`

**Step 5: Commit**

`git commit -m "feat: expose conduit runtime diagnostics"`

### Task 9: Add End-to-End Migration and Compatibility Tests

**Files:**
- Create: `tests/TwitchArchivist.IntegrationTests/EventSubConduitMigrationIntegrationTests.cs`
- Modify: `tests/TwitchArchivist.IntegrationTests/IntegrationTestWebApplicationFactory.cs`
- Test: `tests/TwitchArchivist.IntegrationTests/EventSubConduitMigrationIntegrationTests.cs`

**Step 1: Write failing integration tests**

Cover:
- direct websocket mode still works when conduit mode is off
- conduit mode starts cleanly
- restart in conduit mode preserves subscription ownership
- reconnect only reassigns shards and does not re-create all subscriptions

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests\TwitchArchivist.IntegrationTests\TwitchArchivist.IntegrationTests.csproj --filter EventSubConduitMigrationIntegrationTests`

**Step 3: Add minimal fake Helix + fake shard plumbing needed by the test harness**

Do not depend on live Twitch APIs in CI tests.

**Step 4: Run integration tests**

Run:
- `dotnet test tests\TwitchArchivist.IntegrationTests\TwitchArchivist.IntegrationTests.csproj --filter EventSubConduitMigrationIntegrationTests`

**Step 5: Commit**

`git commit -m "test: cover eventsub conduit migration"`

### Task 10: Controlled Cutover and Cleanup

**Files:**
- Modify: `src/TwitchArchivist/Program.cs`
- Modify: `README.md`
- Modify: `docs/plans/2026-04-28-eventsub-conduit-integration.md`

**Step 1: Add feature-flagged startup selection**

Startup should choose:
- direct websocket hosted service when conduit mode is off
- conduit hosted service when conduit mode is on

**Step 2: Document rollout procedure**

Rollout procedure must include:
- enable conduit mode in non-production first
- create conduit with small shard count
- verify assignment and event receipt
- confirm subscription count stabilizes across restart
- decommission legacy direct-websocket subscription logic only after stability

**Step 3: Run full verification**

Run:
- `dotnet test tests\TwitchArchivist.UnitTests\TwitchArchivist.UnitTests.csproj`
- `dotnet test tests\TwitchArchivist.IntegrationTests\TwitchArchivist.IntegrationTests.csproj`
- `dotnet test TwitchArchivist.slnx`

**Step 4: Commit**

`git commit -m "feat: add conduit-backed eventsub rollout path"`

## Design Notes

- Prefer **one conduit, many shards** first. Multiple conduits should be reserved for future partitioning, not initial migration.
- Preserve current event handlers for archive-job creation and live-state tracking. The migration target is transport/control-plane replacement, not event payload redesign.
- Treat shard assignment and subscription creation as separate phases. A shard reconnect must not imply subscription recreation.
- Use app access token APIs for conduit management. Keep user token usage only where Twitch requires it elsewhere.
- Log every conduit mutation with conduit id, shard id, session id, subscription type, broadcaster user id, and retry attempt.

## Risks

- TwitchLib may not expose all conduit-relevant WebSocket lifecycle details cleanly for multi-shard use; a lower-level WebSocket client may become necessary.
- SQLite persistence is fine for single-process state, but multi-process shard orchestration would require stronger coordination if introduced later.
- Migration cleanup of old direct-websocket subscriptions must be staged carefully to avoid losing live events during cutover.

## Recommended Execution Order

1. Persistence and config
2. Helix conduit APIs
3. Shard-aware websocket abstraction
4. Conduit hosted service
5. Conduit-based subscription reconciler
6. Failure handling and diagnostics
7. Integration tests
8. Flagged rollout

## Implementation Status

Completed on `2026-04-28`:

- Tasks 1 through 10 are implemented.
- Conduit mode is feature-flagged behind `Twitch:EventSubTransportMode = conduit-websocket`.
- Shard assignment repair, stale direct-subscription cleanup, and `429` / `Retry-After` handling are covered.
- Runtime diagnostics now expose conduit id, shard counts, last assignment error, last subscription reconcile error, and last rate-limit timestamp.
- Integration coverage now verifies:
  - direct websocket reconciliation still works when conduit mode is off
  - conduit startup creates and assigns shards
  - conduit restarts preserve subscription ownership
  - shard reconnect repair reassigns transport without recreating conduit subscriptions
