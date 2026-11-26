using StorageAgent.Transport;
using System.Text.Json;
using StorageAgent.Contracts;
using StorageAgent.Core;
using StorageAgent.Service;

namespace StorageAgent.Tests;

public sealed class ContractAndPrivacyTests
{
    [Fact]
    public void ContractHasExplicitMetricAvailabilityAndNoSerialField()
    {
        var disk = new StorageDisk("0123456789ABCDEF0123456789ABCDEF01234567", StorageIdentityConfidence.HardwareId,
            0, "Model", Metric<string>.Unavailable("driver"), Metric<string>.Available("Model", "descriptor"),
            Metric<string>.Unavailable("driver"), Metric<long>.Available(1000, "ioctl"), StorageTechnology.Unknown,
            StorageTransport.Unknown, Metric<int>.Unavailable("driver"), StorageHealth.Unknown, null,
            Metric<int>.Unavailable("smart"), Metric<int>.Unavailable("smart"), Metric<int>.Unavailable("smart"),
            Metric<int>.Unavailable("smart"), Metric<int>.Unavailable("smart"), Metric<int>.Unavailable("smart"),
            Metric<int>.Unavailable("smart"), Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"),
            Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"),
            Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"),
            Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"),
            Metric<string>.Unavailable("smart"), Metric<string>.Unavailable("smart"), Metric<bool>.Unavailable("smart"),
            [], [], DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(disk, StorageContractJson.Options);
        Assert.Contains("\"status\":\"unavailable\"", json);
        Assert.DoesNotContain("serial", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("temperatureC").GetProperty("value").ValueKind);
        var roundTrip = JsonSerializer.Deserialize<StorageDisk>(json, StorageContractJson.Options)!;
        Assert.Equal(MetricStatus.Unavailable, roundTrip.TemperatureC.Status);
        Assert.False(roundTrip.TemperatureC.HasValue);
    }

    [Fact]
    public void DiskIdentityIsMachineLocalPseudonymousAndStableForSameHardwareKey()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "identity.key");
        var store = new MachineIdentityStore(keyPath, new TestIdentityProtector());
        var first = store.CreateDiskId("serial|private-serial|model");
        var same = store.CreateDiskId("serial|private-serial|model");
        var otherDisk = store.CreateDiskId("serial|other-serial|model");
        var afterRestart = new MachineIdentityStore(keyPath, new TestIdentityProtector()).CreateDiskId("serial|private-serial|model");

        Assert.Matches("^[0-9A-F]{40}$", first);
        Assert.Equal(first, same);
        Assert.Equal(first, afterRestart);
        Assert.NotEqual(first, otherDisk);
        Assert.Equal(store.CreateAgentId("https://one.example"), store.CreateAgentId("https://one.example/"));
        Assert.NotEqual(store.CreateAgentId("https://one.example"), store.CreateAgentId("https://two.example"));
        Assert.NotEqual(store.CreateRemoteDeviceId("https://one.example", "disk", first),
            store.CreateRemoteDeviceId("https://two.example", "disk", first));
        Assert.DoesNotContain("private-serial", first, StringComparison.Ordinal);
        Directory.Delete(Path.GetDirectoryName(keyPath)!, recursive: true);
    }

    [Fact]
    public void RemoteSnapshotOmitsLocalPathsAndUserDefinedVolumeLabels()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "identity.key");
        var identity = new MachineIdentityStore(keyPath, new TestIdentityProtector());
        var disk = TestDisk("local-disk-id");
        var snapshot = new StorageSnapshot(StorageProtocol.Major, StorageProtocol.Minor, DateTimeOffset.UtcNow,
            [disk],
            [new StorageVolume("local-volume-id", "private-label", "NTFS", StorageVolumeKind.Local,
                [@"C:\Users\Person\Mount"], Metric<long>.Available(1024, "test"),
                Metric<long>.Available(512, "test"), Metric<long>.Available(512, "test"), [disk.Id], null)], []);

        var remote = RemoteTelemetrySanitizer.Snapshot(snapshot, "https://one.example", identity);
        var json = JsonSerializer.Serialize(remote, StorageContractJson.Options);
        Assert.Empty(Assert.Single(remote.Volumes).MountPoints);
        Assert.Null(Assert.Single(remote.Volumes).Label);
        Assert.NotEqual(disk.Id, Assert.Single(remote.Disks).Id);
        Assert.Null(Assert.Single(remote.Disks).DeviceNumber);
        Assert.DoesNotContain("C:\\Users\\Person", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-label", json, StringComparison.Ordinal);
        Directory.Delete(Path.GetDirectoryName(keyPath)!, recursive: true);
    }

    [Fact]
    public void NetworkVolumeHasNoPhysicalDiskAssociation()
    {
        var volume = new StorageVolume("id", "nas", null, StorageVolumeKind.Network, ["\\\\server\\share\\"],
            Metric<long>.Unavailable("network volumes are not queried"), Metric<long>.Unavailable("network volumes are not queried"),
            Metric<long>.Unavailable("network volumes are not queried"), [], "Remote NAS SMART is not queried locally.");
        Assert.Equal(StorageVolumeKind.Network, volume.Kind);
        Assert.Empty(volume.PhysicalDiskIds);
    }

    [Fact]
    public void ProtocolMajorIsVersionedAndRefreshIsExplicit()
    {
        var request = new StorageRequest(StorageProtocol.Major, StorageProtocol.Minor, "id", StorageOperation.Refresh, Force: true);
        var json = JsonSerializer.Serialize(request, StorageContractJson.Options);
        var roundTrip = JsonSerializer.Deserialize<StorageRequest>(json, StorageContractJson.Options)!;
        Assert.Equal(StorageOperation.Refresh, roundTrip.Operation);
        Assert.True(roundTrip.Force);
    }

    private sealed class TestIdentityProtector : IMachineKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Select(static value => (byte)(value ^ 0x27)).ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray().Select(static value => (byte)(value ^ 0x27)).ToArray();
    }

    private static StorageDisk TestDisk(string id) => new(id, StorageIdentityConfidence.HardwareId, 0, "Test disk",
        Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"),
        Metric<long>.Unavailable("test"), StorageTechnology.Unknown, StorageTransport.Unknown,
        Metric<int>.Unavailable("test"), StorageHealth.Unknown, null, Metric<int>.Unavailable("test"),
        Metric<int>.Unavailable("test"), Metric<int>.Unavailable("test"), Metric<int>.Unavailable("test"),
        Metric<int>.Unavailable("test"), Metric<int>.Unavailable("test"), Metric<int>.Unavailable("test"),
        Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"),
        Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"),
        Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"),
        Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"), Metric<string>.Unavailable("test"),
        Metric<string>.Unavailable("test"), Metric<bool>.Unavailable("test"), [], [], DateTimeOffset.UtcNow);
}
