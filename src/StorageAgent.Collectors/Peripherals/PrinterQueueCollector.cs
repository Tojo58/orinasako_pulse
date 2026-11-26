using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using StorageAgent.Contracts;

namespace StorageAgent.Collectors;

internal sealed class PrinterQueueCollector(ISystemCollector inventory) : ISystemCollector
{
    public SystemCategory Category => SystemCategory.Peripherals;
    public TimeSpan DefaultInterval => inventory.DefaultInterval;

    public SystemSection Collect(CancellationToken token)
    {
        var section = inventory.Collect(token);
        token.ThrowIfCancellationRequested();
        const string source = "Windows EnumPrinters (local queues)";
        var value = OperatingSystem.IsWindows() ? CountJobs(token) : null;
        var reading = value is { } count
            ? Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(count), source)
            : Metric<JsonElement>.Unavailable(source);
        var probe = new SystemProbe("printQueues", source, reading.Status,
            [[new SystemMetric("queuedJobs", "jobs", reading)]], ObservedAt: DateTimeOffset.UtcNow);
        return section with { Probes = [..section.Probes, probe], ObservedAt = DateTimeOffset.UtcNow };
    }

    [SupportedOSPlatform("windows")]
    private static uint? CountJobs(CancellationToken token)
    {
        const uint local = 2, level = 2, maximumBytes = 8 * 1024 * 1024;
        if (EnumPrinters(local, null, level, IntPtr.Zero, 0, out var bytes, out var count) && bytes == 0) return 0;
        if (Marshal.GetLastWin32Error() != 122 || bytes == 0 || bytes > maximumBytes) return null;
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (!EnumPrinters(local, null, level, buffer, bytes, out _, out count)) return null;
            // PRINTER_INFO_2: 13 pointer fields followed by 8 DWORDs; cJobs is DWORD 6.
            int stride = 13 * IntPtr.Size + 8 * sizeof(uint);
            if (count > bytes / (uint)stride) return null;
            ulong jobs = 0;
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                jobs += unchecked((uint)Marshal.ReadInt32(buffer, index * stride + 13 * IntPtr.Size + 6 * sizeof(uint)));
                if (jobs > 1_000_000) return null;
            }
            return (uint)jobs;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint bytes, out uint needed, out uint returned);
}
