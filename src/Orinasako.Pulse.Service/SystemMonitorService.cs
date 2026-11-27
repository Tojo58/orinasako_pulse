using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orinasako.Pulse.Collectors;
using Orinasako.Pulse.Contracts;

namespace Orinasako.Pulse.Service;

public sealed class SystemRuntimeState
{
    private readonly ConcurrentDictionary<SystemCategory, SystemSection> _sections = new();
    public void Set(SystemSection section) => _sections[section.Category] = section;
    public SystemTelemetry Snapshot()
    {
        var sections = _sections.Values.OrderBy(s => s.Category).ToArray();
        return new(1, sections.Length == 0 ? DateTimeOffset.MinValue : sections.Max(s => s.ObservedAt), sections);
    }
}

internal sealed class SystemMonitorService(SystemRuntimeState state, IConfiguration configuration,
    ILogger<SystemMonitorService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(WindowsSystemCollectors.Create().Select(collector => RunAsync(collector, stoppingToken)));

    private async Task RunAsync(ISystemCollector collector, CancellationToken token)
    {
        if (bool.TryParse(configuration["Monitoring:Enabled:" + collector.Category], out var enabled) && !enabled)
        {
            state.Set(new(collector.Category, DateTimeOffset.UtcNow, [new("collector", "configuration", MetricStatus.Unavailable, [], "Disabled by local administrator.")]));
            return;
        }
        var seconds = int.TryParse(configuration["Monitoring:Intervals:" + collector.Category], out var configured)
            ? Math.Clamp(configured, 5, 86400) : (int)collector.DefaultInterval.TotalSeconds;
        while (!token.IsCancellationRequested)
        {
            // At most one native request per collector; a stuck provider never starts another worker.
            var work = Task.Run(() => collector.Collect(token), token);
            try { state.Set(await work.WaitAsync(TimeSpan.FromSeconds(25), token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (TimeoutException)
            {
                state.Set(Failed(collector.Category, "Collection deadline exceeded."));
                try { await work.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) { logger.LogDebug("Collector {Category} ended ({Type}).", collector.Category, error.GetType().Name); }
            }
            catch (Exception error)
            {
                state.Set(Failed(collector.Category, "Collection failed."));
                logger.LogWarning("Collector {Category} failed ({Type}).", collector.Category, error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private static SystemSection Failed(SystemCategory category, string detail) =>
        new(category, DateTimeOffset.UtcNow, [new("collector", "Windows", MetricStatus.Error, [], detail)]);
}
