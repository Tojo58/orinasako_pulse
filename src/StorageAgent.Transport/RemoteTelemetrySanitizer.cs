using StorageAgent.Contracts;
using StorageAgent.Core;

namespace StorageAgent.Transport;

internal static class RemoteTelemetrySanitizer
{
    public static StorageSnapshot Snapshot(StorageSnapshot snapshot, string serverUrl, MachineIdentityStore identity)
    {
        var diskIds = snapshot.Disks.ToDictionary(static disk => disk.Id,
            disk => identity.CreateRemoteDeviceId(serverUrl, "disk", disk.Id), StringComparer.Ordinal);
        var disks = snapshot.Disks.Select(disk => disk with
        {
            Id = diskIds[disk.Id],
            DeviceNumber = null
        }).ToArray();
        var volumes = snapshot.Volumes.Select(volume => volume with
        {
            Id = identity.CreateRemoteDeviceId(serverUrl, "volume", volume.Id),
            Label = null,
            MountPoints = [],
            PhysicalDiskIds = volume.PhysicalDiskIds
                .Where(diskIds.ContainsKey)
                .Select(diskId => diskIds[diskId])
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        }).ToArray();
        return snapshot with { Disks = disks, Volumes = volumes };
    }

    public static StorageChange Change(StorageChange change, string serverUrl, MachineIdentityStore identity) =>
        change with { DeviceId = identity.CreateRemoteDeviceId(serverUrl, "disk", change.DeviceId) };

    public static StorageDisk Disk(StorageDisk disk, string serverUrl, MachineIdentityStore identity) =>
        disk with { Id = identity.CreateRemoteDeviceId(serverUrl, "disk", disk.Id), DeviceNumber = null };
}
