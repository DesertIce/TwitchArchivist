using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchArchivist.Models;
using TwitchArchivist.Persistence;
using TwitchArchivist.Persistence.Entities;

namespace TwitchArchivist.Services.Twitch;

public class EventSubConduitCoordinator(
    ITwitchHelixClient twitchHelixClient,
    IEventSubWebsocketClient websocketClientFactory,
    IServiceScopeFactory scopeFactory,
    RuntimeStatusStore runtimeStatusStore,
    IOptions<TwitchOptions> twitchOptions,
    ILogger<EventSubConduitCoordinator> logger,
    TimeProvider timeProvider) : IEventSubConduitCoordinator
{
    private static readonly Uri EventSubEndpoint = new("wss://eventsub.wss.twitch.tv/ws");
    private readonly Dictionary<int, IEventSubShardClient> _shardClients = [];
    private readonly SemaphoreSlim _sync = new(1, 1);
    private bool _started;
    private string? _lastShardAssignmentError;
    private DateTimeOffset? _lastRateLimitUtc;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            runtimeStatusStore.UpdateEventSubConnectionState("conduit-connecting");
            var configuredShardCount = Math.Max(1, twitchOptions.Value.EventSubConduitShardCount);
            var conduit = await EnsureConduitAsync(configuredShardCount, cancellationToken);
            var connectedShards = await EnsureShardClientsAsync(conduit, configuredShardCount, cancellationToken);
            await AssignShardsAsync(conduit, connectedShards, cancellationToken);

            _started = true;
            runtimeStatusStore.UpdateEventSubConnectionState("conduit-connected");
        }
        catch
        {
            runtimeStatusStore.UpdateEventSubConnectionState("conduit-error");
            throw;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!_started)
            {
                return;
            }

            var configuredShardCount = Math.Max(1, twitchOptions.Value.EventSubConduitShardCount);
            var conduit = await ResolveExistingConduitAsync(cancellationToken);
            if (conduit is null)
            {
                return;
            }

            var remoteConduit = (await twitchHelixClient.GetEventSubConduitsAsync(cancellationToken))
                .SingleOrDefault(x => string.Equals(x.Id, conduit.TwitchConduitId, StringComparison.Ordinal));
            if (remoteConduit is null)
            {
                return;
            }

            var shardsToRepair = new HashSet<int>();
            foreach (var remoteShard in remoteConduit.Shards)
            {
                if (!int.TryParse(remoteShard.ShardId, out var shardId))
                {
                    continue;
                }

                if (!string.Equals(remoteShard.Status, "enabled", StringComparison.OrdinalIgnoreCase))
                {
                    shardsToRepair.Add(shardId);
                    continue;
                }

                if (_shardClients.TryGetValue(shardId, out var shardClient))
                {
                    if (!shardClient.ConnectionState.IsConnected ||
                        !string.Equals(shardClient.SessionId, remoteShard.TransportSessionId, StringComparison.Ordinal))
                    {
                        shardsToRepair.Add(shardId);
                    }
                }
                else
                {
                    shardsToRepair.Add(shardId);
                }
            }

            if (shardsToRepair.Count == 0 && _shardClients.Count == configuredShardCount)
            {
                await UpdateRuntimeStatusAsync(conduit.TwitchConduitId, configuredShardCount, remoteConduit.Shards, cancellationToken);
                return;
            }

            var repairedAssignments = new List<ConnectedShardAssignment>();
            foreach (var shardId in shardsToRepair)
            {
                repairedAssignments.Add(await ReplaceShardAsync(shardId, cancellationToken));
            }

            if (repairedAssignments.Count > 0)
            {
                await AssignShardsAsync(conduit, repairedAssignments, cancellationToken);
            }
            else
            {
                await UpdateRuntimeStatusAsync(conduit.TwitchConduitId, configuredShardCount, remoteConduit.Shards, cancellationToken);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            foreach (var shardClient in _shardClients.Values)
            {
                try
                {
                    await shardClient.DisconnectAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to disconnect EventSub conduit shard client {ShardKey}", shardClient.ShardKey);
                }
            }

            _shardClients.Clear();
            _started = false;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<EventSubConduit> EnsureConduitAsync(int configuredShardCount, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var configuredConduitId = twitchOptions.Value.EventSubConduitId;
        var remoteConduits = await twitchHelixClient.GetEventSubConduitsAsync(cancellationToken);

        EventSubConduitRecord? remoteConduit = null;
        if (!string.IsNullOrWhiteSpace(configuredConduitId))
        {
            remoteConduit = remoteConduits.SingleOrDefault(x => string.Equals(x.Id, configuredConduitId, StringComparison.Ordinal));
        }

        remoteConduit ??= remoteConduits.SingleOrDefault();

        if (remoteConduit is null)
        {
            remoteConduit = await twitchHelixClient.CreateEventSubConduitAsync(configuredShardCount, cancellationToken);
        }
        else if (remoteConduit.ShardCount != configuredShardCount)
        {
            remoteConduit = await twitchHelixClient.UpdateEventSubConduitAsync(remoteConduit.Id, configuredShardCount, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var conduit = await dbContext.EventSubConduits
            .SingleOrDefaultAsync(x => x.TwitchConduitId == remoteConduit.Id, cancellationToken);

        if (conduit is null)
        {
            conduit = new EventSubConduit
            {
                TwitchConduitId = remoteConduit.Id,
                CreatedUtc = now
            };
            dbContext.EventSubConduits.Add(conduit);
        }

        conduit.ShardCount = configuredShardCount;
        conduit.UpdatedUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return conduit;
    }

    private async Task<EventSubConduit?> ResolveExistingConduitAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var configuredConduitId = twitchOptions.Value.EventSubConduitId;
        if (!string.IsNullOrWhiteSpace(configuredConduitId))
        {
            return await dbContext.EventSubConduits.SingleOrDefaultAsync(x => x.TwitchConduitId == configuredConduitId, cancellationToken);
        }

        var conduits = await dbContext.EventSubConduits.ToListAsync(cancellationToken);
        return conduits
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<ConnectedShardAssignment>> EnsureShardClientsAsync(
        EventSubConduit conduit,
        int configuredShardCount,
        CancellationToken cancellationToken)
    {
        var assignments = new List<ConnectedShardAssignment>(configuredShardCount);

        for (var shardId = 0; shardId < configuredShardCount; shardId++)
        {
            if (_shardClients.TryGetValue(shardId, out var existingClient) &&
                existingClient.ConnectionState.IsConnected &&
                !string.IsNullOrWhiteSpace(existingClient.SessionId))
            {
                assignments.Add(new ConnectedShardAssignment(
                    shardId,
                    existingClient.SessionId!,
                    existingClient.ConnectionState.LastWelcomeUtc,
                    "enabled"));
                continue;
            }

            assignments.Add(await ReplaceShardAsync(shardId, cancellationToken));
        }

        return assignments;
    }

    private async Task<ConnectedShardAssignment> ReplaceShardAsync(int shardId, CancellationToken cancellationToken)
    {
        if (_shardClients.Remove(shardId, out var previousClient))
        {
            try
            {
                await previousClient.DisconnectAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to disconnect stale shard client {ShardId}", shardId);
            }
        }

        var shardClient = websocketClientFactory.CreateShardClient(shardId.ToString());
        shardClient.Disconnected += OnShardDisconnectedAsync;
        shardClient.ShardDisabled += OnShardDisabledAsync;
        _shardClients[shardId] = shardClient;

        await shardClient.ConnectAsync(EventSubEndpoint);
        var sessionId = await WaitForSessionIdAsync(shardClient, cancellationToken);

        return new ConnectedShardAssignment(shardId, sessionId, shardClient.ConnectionState.LastWelcomeUtc, "enabled");
    }

    private async Task AssignShardsAsync(
        EventSubConduit conduit,
        IReadOnlyList<ConnectedShardAssignment> connectedShards,
        CancellationToken cancellationToken)
    {
        try
        {
            var assignedShards = await twitchHelixClient.UpdateEventSubConduitShardsAsync(
                conduit.TwitchConduitId,
                connectedShards.Select(x => new EventSubConduitShardRecord(x.ShardId.ToString(), x.Status, x.TransportSessionId)).ToArray(),
                cancellationToken);

            _lastShardAssignmentError = null;
            await PersistShardAssignmentsAsync(conduit, conduit.ShardCount, connectedShards, assignedShards, cancellationToken);
            await UpdateRuntimeStatusAsync(conduit.TwitchConduitId, conduit.ShardCount, assignedShards, cancellationToken);
        }
        catch (TwitchHelixRateLimitException)
        {
            _lastRateLimitUtc = timeProvider.GetUtcNow();
            _lastShardAssignmentError = "rate-limited";
            throw;
        }
        catch (Exception ex)
        {
            _lastShardAssignmentError = ex.Message;
            throw;
        }
    }

    private async Task UpdateRuntimeStatusAsync(
        string conduitId,
        int configuredShardCount,
        IReadOnlyList<EventSubConduitShardRecord> assignedShards,
        CancellationToken cancellationToken)
    {
        var activeShardCount = assignedShards.Count(x => string.Equals(x.Status, "enabled", StringComparison.OrdinalIgnoreCase));
        var disabledShardCount = assignedShards.Count - activeShardCount;
        runtimeStatusStore.UpdateEventSubConduitStatus(
            "conduit-websocket",
            conduitId,
            configuredShardCount,
            activeShardCount,
            disabledShardCount,
            _lastShardAssignmentError,
            runtimeStatusStore.EventSubLastSubscriptionReconcileError,
            _lastRateLimitUtc);

        await Task.CompletedTask;
    }

    private async Task<string> WaitForSessionIdAsync(IEventSubShardClient shardClient, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, twitchOptions.Value.EventSubConduitAssignmentTimeoutSeconds));
        var startedAt = timeProvider.GetUtcNow();

        while (timeProvider.GetUtcNow() - startedAt < timeout)
        {
            if (!string.IsNullOrWhiteSpace(shardClient.SessionId))
            {
                return shardClient.SessionId;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for EventSub shard {shardClient.ShardKey} session id before assignment.");
    }

    private async Task PersistShardAssignmentsAsync(
        EventSubConduit conduit,
        int configuredShardCount,
        IReadOnlyList<ConnectedShardAssignment> connectedShards,
        IReadOnlyList<EventSubConduitShardRecord> assignedShards,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TwitchArchivistDbContext>();
        var persistedConduit = await dbContext.EventSubConduits
            .Include(x => x.Shards)
            .SingleAsync(x => x.Id == conduit.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();

        persistedConduit.ShardCount = configuredShardCount;
        persistedConduit.UpdatedUtc = now;

        foreach (var connectedShard in connectedShards)
        {
            var assignedShard = assignedShards.SingleOrDefault(x => x.ShardId == connectedShard.ShardId.ToString());
            var shard = persistedConduit.Shards.SingleOrDefault(x => x.ShardId == connectedShard.ShardId);
            if (shard is null)
            {
                shard = new EventSubConduitShard
                {
                    ShardId = connectedShard.ShardId,
                    CreatedUtc = now
                };
                persistedConduit.Shards.Add(shard);
            }

            shard.TransportSessionId = assignedShard?.TransportSessionId ?? connectedShard.TransportSessionId;
            shard.Status = assignedShard?.Status ?? connectedShard.Status;
            shard.LastWelcomeUtc = connectedShard.LastWelcomeUtc;
            shard.LastAssignmentUtc = now;
            shard.UpdatedUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task OnShardDisconnectedAsync(object? sender, EventSubDisconnectedEventArgs args)
    {
        runtimeStatusStore.UpdateEventSubConnectionState("conduit-shard-disconnected");
        return Task.CompletedTask;
    }

    private Task OnShardDisabledAsync(object? sender, EventSubShardDisabledEventArgs args)
    {
        runtimeStatusStore.UpdateEventSubConnectionState("conduit-shard-disabled");
        return Task.CompletedTask;
    }

    private sealed record ConnectedShardAssignment(
        int ShardId,
        string TransportSessionId,
        DateTimeOffset? LastWelcomeUtc,
        string Status);
}
