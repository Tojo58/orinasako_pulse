using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using Orinasako.Pulse.Contracts;

namespace Orinasako.Pulse.Collectors;

internal sealed class SecurityCollector(ISystemCollector inner) : ISystemCollector
{
    public SystemCategory Category => inner.Category;
    public TimeSpan DefaultInterval => inner.DefaultInterval;
    public SystemSection Collect(CancellationToken token)
    {
        var section = inner.Collect(token);
        token.ThrowIfCancellationRequested();
        var probe = OperatingSystem.IsWindows() ? ReadSecureBoot() :
            new SystemProbe("secureBoot", "Windows registry", MetricStatus.Unsupported, [], "Windows required.");
        return section with { Probes = [..section.Probes, probe with { ObservedAt = DateTimeOffset.UtcNow }] };
    }

    [SupportedOSPlatform("windows")]
    private static SystemProbe ReadSecureBoot()
    {
        const string source = "HKLM SecureBoot State";
        Metric<JsonElement> metric;
        try
        {
            var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
            metric = value is int enabled && enabled is 0 or 1
                ? Metric<JsonElement>.Available(JsonSerializer.SerializeToElement(enabled == 1), source)
                : Metric<JsonElement>.Unavailable(source, "Firmware state not reported.");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { metric = Metric<JsonElement>.Unavailable(source); }
        return new("secureBoot", source, metric.Status, [[new SystemMetric("enabled", null, metric)]]);
    }
}
