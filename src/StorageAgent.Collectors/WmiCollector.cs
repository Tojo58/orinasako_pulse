using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;
using StorageAgent.Contracts;

namespace StorageAgent.Collectors;

// Only fixed, allowlisted WMI queries and scalar fields. No arbitrary WQL or remote commands.
internal sealed record Field(string Property, string Name, string? Unit = null, bool KelvinTenths = false);
internal sealed record Probe(string Name, string Namespace, string Class, Field[] Fields, string? Where = null, int RefreshSeconds = 0);
internal sealed class WmiCollector(SystemCategory category, int seconds, params Probe[] probes) : ISystemCollector
{
    private readonly Dictionary<string, SystemProbe> _cached = new(StringComparer.Ordinal);
    public SystemCategory Category => category;
    public TimeSpan DefaultInterval => TimeSpan.FromSeconds(seconds);

    public SystemSection Collect(CancellationToken token)
    {
        var output = new List<SystemProbe>();
        foreach (var probe in probes)
        {
            token.ThrowIfCancellationRequested();
            if (probe.RefreshSeconds > 0 && _cached.TryGetValue(probe.Name, out var cached) &&
                cached.ObservedAt is { } observed && DateTimeOffset.UtcNow - observed < TimeSpan.FromSeconds(probe.RefreshSeconds))
            {
                output.Add(cached);
                continue;
            }
            var result = (OperatingSystem.IsWindows() ? Query(probe, token) :
                new SystemProbe(probe.Name, probe.Class, MetricStatus.Unsupported, [], "Windows required."))
                with { ObservedAt = DateTimeOffset.UtcNow };
            if (probe.RefreshSeconds > 0 && result.Status == MetricStatus.Available) _cached[probe.Name] = result;
            output.Add(result);
        }
        return new(Category, DateTimeOffset.UtcNow, output);
    }

    [SupportedOSPlatform("windows")]
    private static SystemProbe Query(Probe probe, CancellationToken token)
    {
        var source = probe.Namespace + ":" + probe.Class;
        try
        {
            var query = "SELECT " + string.Join(",", probe.Fields.Select(f => f.Property).Distinct()) +
                " FROM " + probe.Class + (probe.Where is null ? "" : " WHERE " + probe.Where);
            using var searcher = new ManagementObjectSearcher(new ManagementScope(probe.Namespace),
                new ObjectQuery(query), new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(5) });
            using var results = searcher.Get();
            var rows = new List<IReadOnlyList<SystemMetric>>();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    token.ThrowIfCancellationRequested();
                    if (rows.Count >= 32) return new(probe.Name, source, MetricStatus.Available, rows, "Limited to 32 instances.");
                    var values = new List<SystemMetric>();
                    foreach (var field in probe.Fields)
                    {
                        Metric<JsonElement> metric;
                        try
                        {
                            var value = item[field.Property];
                            if (value is null) metric = Metric<JsonElement>.Unavailable(source);
                            else if (field.KelvinTenths)
                            {
                                var celsius = Convert.ToDouble(value, CultureInfo.InvariantCulture) / 10 - 273.15;
                                metric = celsius is >= -50 and <= 200
                                    ? Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(Math.Round(celsius, 1)), source)
                                    : Metric<JsonElement>.Unavailable(source, "Implausible sensor value.");
                            }
                            else if (value is string str)
                                metric = string.IsNullOrWhiteSpace(str) ? Metric<JsonElement>.Unavailable(source) :
                                    Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(str[..Math.Min(160, str.Length)]), source);
                            else if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double)
                                metric = Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(value), source);
                            else metric = Metric<JsonElement>.Unsupported(source, "Non-scalar value.");
                        }
                        catch (Exception error) when (error is ManagementException or InvalidCastException or FormatException or OverflowException or ArgumentException)
                        { metric = Metric<JsonElement>.Unavailable(source); }
                        values.Add(new(field.Name, field.Unit, metric));
                    }
                    if (probe.Name == "memory") AddMemoryUsage(values, source);
                    rows.Add(values);
                }
            }
            return new(probe.Name, source, rows.Count == 0 ? MetricStatus.Unavailable : MetricStatus.Available, rows);
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return new(probe.Name, source, MetricStatus.Unavailable, [], "Provider or class unavailable."); }
    }

    private static void AddMemoryUsage(List<SystemMetric> values, string source)
    {
        var total = values.FirstOrDefault(v => v.Name == "total")?.Reading;
        var free = values.FirstOrDefault(v => v.Name == "free")?.Reading;
        var used = Metric<JsonElement>.Unavailable(source);
        var percent = Metric<JsonElement>.Unavailable(source);
        if (total?.Status == MetricStatus.Available && free?.Status == MetricStatus.Available &&
            total.Value.TryGetInt64(out var totalKiB) && free.Value.TryGetInt64(out var freeKiB) &&
            totalKiB > 0 && freeKiB >= 0 && freeKiB <= totalKiB)
        {
            used = Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(totalKiB - freeKiB), source + " (derived)");
            percent = Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(Math.Round(100d * (totalKiB - freeKiB) / totalKiB, 2)), source + " (derived)");
        }
        values.Add(new("used", "KiB", used));
        values.Add(new("usage", "percent", percent));
    }
}
