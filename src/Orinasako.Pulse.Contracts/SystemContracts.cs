using System.Text.Json;
using System.Text.Json.Serialization;
namespace Orinasako.Pulse.Contracts;

/// <summary>Independent, versioned system telemetry schema. Storage keeps its existing contract.</summary>
public enum SystemCategory { Cpu, Memory, Gpu, Motherboard, Network, Power, Thermal, OperatingSystem, Processes, Peripherals, Security }
public sealed record SystemMetric(string Name, string? Unit, Metric<JsonElement> Reading);
public sealed record SystemProbe(string Name, string Source, MetricStatus Status,
    IReadOnlyList<IReadOnlyList<SystemMetric>> Rows, string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ObservedAt = null);
public sealed record SystemSection(SystemCategory Category, DateTimeOffset ObservedAt,
    IReadOnlyList<SystemProbe> Probes);
public sealed record SystemTelemetry(int SchemaVersion, DateTimeOffset ObservedAt,
    IReadOnlyList<SystemSection> Sections)
{
    public SystemTelemetry Select(IReadOnlyList<SystemCategory>? allowed)
    {
        var sections = Sections.Where(s => allowed?.Contains(s.Category) == true).ToArray();
        return new(1, sections.Length == 0 ? DateTimeOffset.MinValue : sections.Max(s => s.ObservedAt), sections);
    }
}
public sealed record SystemReport(StorageSnapshot? Storage, SystemTelemetry System);
