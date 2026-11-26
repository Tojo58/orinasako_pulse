using System.Text.Json;
using StorageAgent.Contracts;
using StorageAgent.Service;

namespace StorageAgent.Tests;

public sealed class SystemTelemetryTests
{
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
    public void MissingSystemMetricIsNullNotZero()
    {
        var metric = new SystemMetric("temperature", "C", Metric<JsonElement>.Unavailable("sensor"));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(metric, StorageContractJson.Options));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("reading").GetProperty("value").ValueKind);
    }
}
