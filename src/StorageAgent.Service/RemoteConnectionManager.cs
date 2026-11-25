using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StorageAgent.Contracts;
using StorageAgent.Core;

namespace StorageAgent.Service;

internal sealed class RemoteConnectionManager(
    RemoteConnectionVault vault,
    RemoteProtocolClient remote,
    MachineIdentityStore identity,
    StorageRuntimeState state,
    IStorageCollector collector,
    ILogger<RemoteConnectionManager> logger)
{
    private const int MaximumQueuedChanges = 1000;
    private readonly string _agentVersion = typeof(RemoteConnectionManager).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private readonly ConcurrentDictionary<string, RetryState> _retries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RemoteConnectionInfo> List() => vault.List();

    public async Task<RemoteConnectionInfo> ConnectAsync(string serverUrl, string code, CancellationToken token)
    {
        var connection = await remote.EnrollAsync(serverUrl, code, identity.CreateAgentId(serverUrl), _agentVersion, token);
        vault.Add(connection);
        return connection.Info;
    }

    public async Task DisconnectAsync(string connectionId, CancellationToken token)
    {
        var connection = vault.Find(connectionId)
            ?? throw new RemoteProtocolException("connection.not_found", "The requested server connection was not found.");
        vault.Update(connectionId, current => current with
        {
            Info = current.Info with { State = RemoteConnectionState.RevocationPending, LastError = null },
            PendingSnapshot = null,
            PendingEvents = [],
            PendingCommandResults = []
        });
        connection = vault.Find(connectionId)!;
        try
        {
            await remote.RevokeAsync(connection, token);
            vault.Remove(connectionId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            RecordFailure(connectionId, error);
            logger.LogWarning("Remote credential revocation is queued for retry ({Code}).", SafeCode(error));
        }
    }

    public void QueueChange(StorageChange change)
    {
        foreach (var connection in vault.ReadAll())
        {
            if (connection.Info.State != RemoteConnectionState.Connected) continue;
            var specific = new RemoteChangeEnvelope(Guid.NewGuid().ToString(), connection.Info.AgentId,
                RemoteTelemetrySanitizer.Change(change, connection.Info.ServerUrl, identity));
            var queueFull = false;
            vault.Update(connection.Info.ConnectionId, current =>
            {
                if (current.PendingEvents.Count >= MaximumQueuedChanges)
                {
                    queueFull = true;
                    return current with { Info = current.Info with { LastError = "queue.change_capacity_reached" } };
                }
                return current with { PendingEvents = current.PendingEvents.Append(specific).ToArray() };
            });
            if (queueFull)
                logger.LogWarning("Remote event queue reached its configured capacity for connection {ConnectionId}; the current snapshot remains available for reconciliation.", connection.Info.ConnectionId);
        }
    }

    public async Task<RemoteSyncResult> SynchronizeAsync(CancellationToken token)
    {
        var snapshotCount = 0;
        var eventCount = 0;
        var commandCount = 0;
        foreach (var item in vault.ReadAll())
        {
            token.ThrowIfCancellationRequested();
            if (_retries.TryGetValue(item.Info.ConnectionId, out var retry) && retry.NextAttemptAt > DateTimeOffset.UtcNow)
                continue;
            if (item.Info.State == RemoteConnectionState.RevocationPending)
            {
                try
                {
                    await remote.RevokeAsync(item, token);
                    vault.Remove(item.Info.ConnectionId);
                    _retries.TryRemove(item.Info.ConnectionId, out _);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { RecordFailure(item.Info.ConnectionId, error); ScheduleRetry(item.Info.ConnectionId, error); }
                continue;
            }
            if (item.Info.State != RemoteConnectionState.Connected) continue;
            try
            {
                var latest = state.Snapshot;
                var connection = item;
                if (latest is not null && (connection.PendingSnapshot is not null ||
                    connection.Info.LastSnapshotAt is null || latest.ObservedAt > connection.Info.LastSnapshotAt.Value))
                {
                    if (connection.PendingSnapshot is null || connection.PendingSnapshot.Snapshot.ObservedAt < latest.ObservedAt)
                    {
                        var remoteSnapshot = RemoteTelemetrySanitizer.Snapshot(latest, connection.Info.ServerUrl, identity);
                        connection = connection with
                        {
                            PendingSnapshot = new RemoteSnapshotEnvelope(Guid.NewGuid().ToString(), connection.Info.AgentId, remoteSnapshot)
                        };
                        vault.Update(connection.Info.ConnectionId, current => current with { PendingSnapshot = connection.PendingSnapshot });
                    }
                    if (connection.PendingSnapshot is { } pending)
                    {
                        connection = await remote.EnsureTokenAsync(connection, token);
                        await remote.SendSnapshotAsync(connection, pending, token);
                        vault.Update(connection.Info.ConnectionId, current => current with
                        {
                            PendingSnapshot = current.PendingSnapshot?.DeliveryId == pending.DeliveryId ? null : current.PendingSnapshot,
                            Info = current.Info with
                            {
                                LastSyncAt = DateTimeOffset.UtcNow,
                                LastSnapshotAt = pending.Snapshot.ObservedAt,
                                LastError = null
                            }
                        });
                        snapshotCount++;
                    }
                }

                connection = vault.Find(item.Info.ConnectionId)!;
                if (connection.PendingEvents.Count > 0)
                {
                    connection = await remote.EnsureTokenAsync(connection, token);
                    var acknowledgements = await remote.SendEventsAsync(connection,
                        new RemoteChangeBatch(connection.Info.AgentId, connection.PendingEvents), token);
                    var accepted = acknowledgements.ToHashSet(StringComparer.Ordinal);
                    vault.Update(connection.Info.ConnectionId, current => current with
                    {
                        PendingEvents = current.PendingEvents.Where(change => !accepted.Contains(change.EventId)).ToArray(),
                        Info = current.Info with { LastSyncAt = DateTimeOffset.UtcNow, LastError = null }
                    });
                    eventCount += accepted.Count;
                }

                connection = vault.Find(item.Info.ConnectionId)!;
                foreach (var pendingResult in connection.PendingCommandResults ?? [])
                {
                    connection = await remote.EnsureTokenAsync(connection, token);
                    await remote.SendCommandResultAsync(connection, pendingResult, token);
                    vault.Update(connection.Info.ConnectionId, current => current with
                    {
                        PendingCommandResults = (current.PendingCommandResults ?? [])
                            .Where(result => !string.Equals(result.CommandId, pendingResult.CommandId, StringComparison.Ordinal))
                            .ToArray(),
                        ProcessedCommandIds = current.ProcessedCommandIds.Contains(pendingResult.CommandId, StringComparer.Ordinal)
                            ? current.ProcessedCommandIds
                            : current.ProcessedCommandIds.Append(pendingResult.CommandId).TakeLast(MaximumQueuedChanges).ToArray(),
                        Info = current.Info with { LastSyncAt = DateTimeOffset.UtcNow, LastError = null }
                    });
                    connection = vault.Find(item.Info.ConnectionId)!;
                }
                connection = await remote.EnsureTokenAsync(connection, token);
                var commands = await remote.PollCommandsAsync(connection, token);
                foreach (var command in commands)
                {
                    if (command.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(command.CommandId) ||
                        command.CommandId.Length > 120 ||
                        !Guid.TryParseExact(command.CommandId, "D", out _))
                        continue;
                    var current = vault.Find(connection.Info.ConnectionId)!;
                    if (!RemoteCommandReplayGuard.ShouldExecute(current, command.CommandId))
                        continue;
                    var result = await ExecuteCommandAsync(command, current, token);
                    vault.Update(current.Info.ConnectionId, stored => stored with
                    {
                        PendingCommandResults = (stored.PendingCommandResults ?? [])
                            .Where(pending => !string.Equals(pending.CommandId, result.CommandId, StringComparison.Ordinal))
                            .Append(result)
                            .ToArray()
                    });
                    var pendingConnection = vault.Find(current.Info.ConnectionId)!;
                    var durableResult = pendingConnection.PendingCommandResults?.FirstOrDefault(pending =>
                        string.Equals(pending.CommandId, result.CommandId, StringComparison.Ordinal)) ?? result;
                    pendingConnection = await remote.EnsureTokenAsync(pendingConnection, token);
                    await remote.SendCommandResultAsync(pendingConnection, durableResult, token);
                    vault.Update(connection.Info.ConnectionId, stored => stored with
                    {
                        PendingCommandResults = (stored.PendingCommandResults ?? [])
                            .Where(pending => !string.Equals(pending.CommandId, result.CommandId, StringComparison.Ordinal))
                            .ToArray(),
                        ProcessedCommandIds = stored.ProcessedCommandIds.Contains(command.CommandId, StringComparer.Ordinal)
                            ? stored.ProcessedCommandIds
                            : stored.ProcessedCommandIds.Append(command.CommandId).TakeLast(MaximumQueuedChanges).ToArray(),
                        Info = stored.Info with { LastSyncAt = DateTimeOffset.UtcNow, LastError = null }
                    });
                    commandCount++;
                }
                _retries.TryRemove(item.Info.ConnectionId, out _);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                RecordFailure(item.Info.ConnectionId, error);
                ScheduleRetry(item.Info.ConnectionId, error);
                logger.LogInformation("Remote connection {ConnectionId} sync will retry ({Code}).", item.Info.ConnectionId, SafeCode(error));
            }
        }
        return new RemoteSyncResult(snapshotCount, eventCount, commandCount, DateTimeOffset.UtcNow);
    }

    private async Task<RemoteCommandResult> ExecuteCommandAsync(RemoteCommand command,
        StoredRemoteConnection connection, CancellationToken token)
    {
        try
        {
            switch (command.Kind)
            {
                case RemoteCommandKind.RefreshInventory:
                {
                    var snapshot = await state.RefreshAsync(collector, force: true, token);
                    return new RemoteCommandResult(command.CommandId, true, null,
                        RemoteTelemetrySanitizer.Snapshot(snapshot, connection.Info.ServerUrl, identity), null,
                        DateTimeOffset.UtcNow);
                }
                case RemoteCommandKind.ReadDiskDiagnostics:
                {
                    if (string.IsNullOrWhiteSpace(command.DeviceId))
                        return new RemoteCommandResult(command.CommandId, false, "command.device_id_required", null, null, DateTimeOffset.UtcNow);
                    var snapshot = await state.RefreshAsync(collector, force: true, token);
                    var disk = snapshot.Disks.FirstOrDefault(item =>
                        string.Equals(identity.CreateRemoteDeviceId(connection.Info.ServerUrl, "disk", item.Id),
                            command.DeviceId, StringComparison.Ordinal));
                    return disk is null
                        ? new RemoteCommandResult(command.CommandId, false, "device.not_found", null, null, DateTimeOffset.UtcNow)
                        : new RemoteCommandResult(command.CommandId, true, null, null,
                            RemoteTelemetrySanitizer.Disk(disk, connection.Info.ServerUrl, identity),
                            DateTimeOffset.UtcNow);
                }
                default:
                    return new RemoteCommandResult(command.CommandId, false, "command.unsupported", null, null, DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Read-only remote diagnostic command failed ({Type}, 0x{HResult:X8}).", error.GetType().Name, error.HResult);
            return new RemoteCommandResult(command.CommandId, false, "diagnostic.collection_failed", null, null, DateTimeOffset.UtcNow);
        }
    }


    private void RecordFailure(string connectionId, Exception error)
    {
        var code = SafeCode(error);
        vault.Update(connectionId, current => current with
        {
            Info = current.Info with { LastError = code }
        });
    }

    private static string SafeCode(Exception error) => error switch
    {
        RemoteProtocolException remoteError => remoteError.Code,
        _ => $"connection.error_{error.HResult:X8}"
    };

    private void ScheduleRetry(string connectionId, Exception error)
    {
        var previous = _retries.TryGetValue(connectionId, out var existing) ? existing.Attempts : 0;
        var attempts = Math.Min(previous + 1, 10);
        var baseSeconds = error is RemoteProtocolException { RetryAfter: { } retryAfter }
            ? Math.Clamp(retryAfter.TotalSeconds, 1, 3600)
            : Math.Min(15 * 60, Math.Pow(2, attempts) * 5);
        var jitter = RandomNumberGenerator.GetInt32(0, 5000) / 1000d;
        _retries[connectionId] = new RetryState(attempts, DateTimeOffset.UtcNow.AddSeconds(baseSeconds + jitter));
    }

    private sealed record RetryState(int Attempts, DateTimeOffset NextAttemptAt);
}

internal static class RemoteCommandReplayGuard
{
    public static bool ShouldExecute(StoredRemoteConnection connection, string commandId) =>
        !connection.ProcessedCommandIds.Contains(commandId, StringComparer.Ordinal) &&
        !(connection.PendingCommandResults ?? []).Any(result =>
            string.Equals(result.CommandId, commandId, StringComparison.Ordinal));
}
