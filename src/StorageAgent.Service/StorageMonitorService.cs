using System.Management;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StorageAgent.Core;

namespace StorageAgent.Service;

public sealed class StorageMonitorService(
    IStorageCollector collector,
    StorageRuntimeState state,
    ILogger<StorageMonitorService> logger) : BackgroundService
{
    private readonly Channel<byte> _deviceChanges = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private ManagementEventWatcher? _watcher;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.InitializeAsync(stoppingToken);
        StartDeviceNotifications();
        await RefreshSafelyAsync(force: true, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var change = _deviceChanges.Reader.ReadAsync(stoppingToken).AsTask();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(tick, change);
                if (completed == tick)
                {
                    if (!await tick) break;
                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                else
                {
                    await change;
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    while (_deviceChanges.Reader.TryRead(out _)) { }
                    change = _deviceChanges.Reader.ReadAsync(stoppingToken).AsTask();
                }
                await RefreshSafelyAsync(force: true, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            StopDeviceNotifications();
        }
    }

    private void StopDeviceNotifications()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _watcher?.Stop(); } catch (ManagementException) { }
        _watcher?.Dispose();
        _watcher = null;
    }

    private void StartDeviceNotifications()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3"));
            _watcher.EventArrived += (_, _) => _deviceChanges.Writer.TryWrite(1);
            _watcher.Start();
        }
        catch (ManagementException error)
        {
            logger.LogWarning("Windows device-change notifications are unavailable ({Code}); the periodic inventory remains active.", error.ErrorCode);
            _watcher?.Dispose();
            _watcher = null;
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            logger.LogWarning("Windows device-change notifications are unavailable (0x{HResult:X8}); the periodic inventory remains active.", error.HResult);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private async Task RefreshSafelyAsync(bool force, CancellationToken token)
    {
        try { await state.RefreshAsync(collector, force, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Storage inventory refresh failed ({Type}, 0x{HResult:X8}); cached data is retained.", error.GetType().Name, error.HResult);
        }
    }
}
