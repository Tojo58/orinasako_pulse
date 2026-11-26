using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StorageAgent.Contracts;

namespace StorageAgent.Service;

internal sealed class RemoteSyncService(
    RemoteConnectionManager connections,
    StorageChangeHub changes,
    ILogger<RemoteSyncService> logger,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = int.TryParse(configuration["Monitoring:RemoteSyncSeconds"], out var configured) ? Math.Clamp(configured, 30, 86400) : 60;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var change = await changes.WaitAsync(TimeSpan.FromSeconds(seconds), stoppingToken);
                if (change is not null) connections.QueueChange(change);
                try
                {
                    var result = await connections.SynchronizeAsync(stoppingToken);
                    if (result.SnapshotsSent + result.EventsSent + result.CommandsProcessed > 0)
                        logger.LogDebug("Remote synchronization completed ({Snapshots} snapshots, {Events} events, {Commands} read-only commands).",
                            result.SnapshotsSent, result.EventsSent, result.CommandsProcessed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    logger.LogWarning("Remote synchronization cycle failed ({Type}, 0x{HResult:X8}); it will retry.", error.GetType().Name, error.HResult);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
