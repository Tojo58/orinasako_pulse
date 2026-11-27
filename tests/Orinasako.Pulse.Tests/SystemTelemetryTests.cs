using System.Text.Json;
using Orinasako.Pulse.Contracts;
using Orinasako.Pulse.Collectors;
using Orinasako.Pulse.Service;

namespace Orinasako.Pulse.Tests;

public sealed class SystemTelemetryTests
{
    [Fact]
    public void GpuEngineAggregationDoesNotExposeProcessOrAdapterIdentifiers()
    {
        var result = GpuCollector.BusiestEngine([
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 40d),
            ("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 35d),
            ("pid_3_luid_0x0_0x1_phys_0_eng_1_engtype_Copy", 50d),
            ("invalid", 100d)
        ]);
        Assert.Equal(75d, result);
        Assert.Null(GpuCollector.BusiestEngine([("invalid", 50d)]));
    }

    [Fact]
    public void EverySystemCategoryHasExactlyOneCollector()
    {
        var collectors = WindowsSystemCollectors.Create();
        Assert.Equal(Enum.GetValues<SystemCategory>().OrderBy(x => x).ToArray(), collectors.Select(x => x.Category).OrderBy(x => x).ToArray());
        Assert.All(collectors, c => Assert.True(c.DefaultInterval >= TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ScopeDefaultsToNoSystemDataAndNeverIncludesOtherCategories()
    {
        var state = new SystemRuntimeState();
        var now = DateTimeOffset.UtcNow;
        state.Set(new(SystemCategory.Cpu, now, []));
        state.Set(new(SystemCategory.Security, now, []));
        Assert.Empty(state.Snapshot().Select(null).Sections);
        Assert.Equal(SystemCategory.Cpu, Assert.Single(state.Snapshot().Select([SystemCategory.Cpu]).Sections).Category);
    }

    [Fact]
    public void LegacyEnvelopeDoesNotAddSystemProperty()
    {
        var snapshot = new StorageSnapshot(1, 0, DateTimeOffset.UtcNow, [], [], []);
        var legacy = new RemoteSnapshotEnvelope("delivery", "agent", snapshot);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(legacy, StorageContractJson.Options));
        Assert.False(document.RootElement.TryGetProperty("system", out _));
    }

    [Fact]
    public void SystemOnlyEnvelopeExplicitlyOmitsStorage()
    {
        var snapshot = new StorageSnapshot(1, 0, DateTimeOffset.UtcNow, [], [], []);
        var telemetry = new SystemTelemetry(1, DateTimeOffset.UtcNow, [new(SystemCategory.Cpu, DateTimeOffset.UtcNow, [])]);
        var envelope = new RemoteSnapshotEnvelope("delivery", "agent", snapshot, telemetry, false);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, StorageContractJson.Options));
        Assert.False(document.RootElement.GetProperty("storageIncluded").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("snapshot").GetProperty("disks").EnumerateArray());
        Assert.Equal("cpu", document.RootElement.GetProperty("system").GetProperty("sections")[0].GetProperty("category").GetString());
    }

    [Fact]
    public void MissingSystemMetricIsNullNotZero()
    {
        var metric = new SystemMetric("temperature", "C", Metric<JsonElement>.Unavailable("sensor"));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(metric, StorageContractJson.Options));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("reading").GetProperty("value").ValueKind);
    }
}
