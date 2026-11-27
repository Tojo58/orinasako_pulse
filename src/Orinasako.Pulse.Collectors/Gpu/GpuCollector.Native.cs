using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Orinasako.Pulse.Contracts;

namespace Orinasako.Pulse.Collectors;

internal sealed class GpuCollector(ISystemCollector inventory) : ISystemCollector
{
    private SystemProbe? _adapters;
    public SystemCategory Category => SystemCategory.Gpu;
    public TimeSpan DefaultInterval => inventory.DefaultInterval;

    public SystemSection Collect(CancellationToken token)
    {
        var section = inventory.Collect(token);
        token.ThrowIfCancellationRequested();
        if (_adapters?.ObservedAt is not { } at || DateTimeOffset.UtcNow - at >= TimeSpan.FromMinutes(5))
            _adapters = (OperatingSystem.IsWindows() ? ReadAdapters(token) : Unavailable("physicalAdapters", "DXGI", MetricStatus.Unsupported))
                with { ObservedAt = DateTimeOffset.UtcNow };
        token.ThrowIfCancellationRequested();
        var utilization = (OperatingSystem.IsWindows() ? ReadUtilization(token) : Unavailable("utilization", "Windows GPU engine counters", MetricStatus.Unsupported))
            with { ObservedAt = DateTimeOffset.UtcNow };
        return section with { Probes = [..section.Probes, _adapters!, utilization], ObservedAt = DateTimeOffset.UtcNow };
    }

    private static SystemProbe Unavailable(string name, string source, MetricStatus status = MetricStatus.Unavailable) =>
        new(name, source, status, [], "Hardware or provider unavailable.");

    private static SystemMetric Value<T>(string name, T value, string source, string? unit = null) =>
        new(name, unit, Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(value), source));

    [SupportedOSPlatform("windows")]
    private static SystemProbe ReadAdapters(CancellationToken token)
    {
        const string source = "DXGI IDXGIAdapter1";
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero)
                return Unavailable("physicalAdapters", source);
            var enumerate = Method<EnumAdapters1>(factory, 12);
            var rows = new List<IReadOnlyList<SystemMetric>>();
            for (uint i = 0; i < 32; i++)
            {
                token.ThrowIfCancellationRequested();
                var result = enumerate(factory, i, out var adapter);
                if (result == unchecked((int)0x887A0002))
                    return new("physicalAdapters", source, rows.Count == 0 ? MetricStatus.Unavailable : MetricStatus.Available, rows);
                if (result < 0 || adapter == IntPtr.Zero) return Unavailable("physicalAdapters", source);
                try
                {
                    if (Method<GetDesc1>(adapter, 10)(adapter, out var desc) < 0 || (desc.Flags & 3u) != 0) continue;
                    var name = desc.Description?.Trim('\0', ' ');
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    rows.Add([
                        Value("model", name, source),
                        Value("dedicatedMemory", (ulong)desc.DedicatedVideoMemory, source, "bytes"),
                        Value("sharedMemory", (ulong)desc.SharedSystemMemory, source, "bytes")
                    ]);
                }
                finally { Method<Release>(adapter, 2)(adapter); }
            }
            return new("physicalAdapters", source, rows.Count == 0 ? MetricStatus.Unavailable : MetricStatus.Available, rows, "Limited to 32 adapters.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or COMException or PlatformNotSupportedException)
        { return Unavailable("physicalAdapters", source); }
        finally { if (factory != IntPtr.Zero) Method<Release>(factory, 2)(factory); }
    }

    // Engine instance names contain process IDs and adapter identifiers. They are used
    // only transiently to aggregate utilization; neither names nor identifiers leave this collector.
    internal static double? BusiestEngine(IEnumerable<(string Name, double Value)> samples)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in samples)
        {
            var start = name.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase) || start < 0 ||
                name.IndexOf("_eng_", start, StringComparison.OrdinalIgnoreCase) < 0 ||
                !double.IsFinite(value) || value < 0) continue;
            var key = name[start..];
            engines[key] = engines.GetValueOrDefault(key) + Math.Min(value, 100d);
        }
        return engines.Count == 0 ? null : Math.Round(Math.Clamp(engines.Values.Max(), 0d, 100d), 2);
    }

    [SupportedOSPlatform("windows")]
    private static SystemProbe ReadUtilization(CancellationToken token)
    {
        const string source = "Windows GPU engine counters";
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT Name,UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(5) });
            using var results = searcher.Get();
            var samples = new List<(string, double)>();
            int count = 0;
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 4096) return new("utilization", source, MetricStatus.Unavailable, [], "Provider exceeds the sampling limit.");
                    if (item["Name"] is string name && item["UtilizationPercentage"] is { } value)
                        samples.Add((name, Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                }
            }
            var utilization = BusiestEngine(samples);
            return utilization is { } busy
                ? new("utilization", source, MetricStatus.Available, [[Value("busiestEngine", busy, source, "percent")]])
                : Unavailable("utilization", source);
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or COMException or FormatException or InvalidCastException or OverflowException)
        { return Unavailable("utilization", source); }
    }

    private static T Method<T>(IntPtr instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr self, out AdapterDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(IntPtr self);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }
}
