using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageAgent.Contracts;

namespace StorageAgent.Core;

public interface IStorageCollector
{
    Task<StorageSnapshot> CollectAsync(CancellationToken cancellationToken);
}

/// <summary>Read-only inventory and protocol queries using Windows storage APIs.</summary>
public sealed class WindowsDiskCollector(MachineIdentityStore identities) : IStorageCollector
{
    private const int MaxPhysicalDiskNumber = 63;
    private static readonly SemaphoreSlim NativeWorkers = new(2, 2);
    private static readonly SemaphoreSlim VolumeWorker = new(1, 1);

    public async Task<StorageSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new StorageSnapshot(StorageProtocol.Major, StorageProtocol.Minor, DateTimeOffset.UtcNow,
                [], [], [new StorageDiagnostic("platform.unsupported", "Storage hardware collection is available only on Windows.", "platform", DateTimeOffset.UtcNow)]);

        IReadOnlyList<VolumeRead> volumes = [];
        var volumeError = false;
        if (await VolumeWorker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
        {
            var volumeTask = Task.Run(() =>
            {
                try { return ReadVolumes(); }
                finally { VolumeWorker.Release(); }
            }, CancellationToken.None);
            try { volumes = await volumeTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken); }
            catch (TimeoutException) { volumeError = true; }
        }
        else volumeError = true;
        var reliability = await Task.Run(WindowsStorageReliability.ReadByDiskNumber, cancellationToken);
        var tasks = Enumerable.Range(0, MaxPhysicalDiskNumber + 1)
            .Select(number => ReadDiskWithTimeoutAsync(number, reliability.GetValueOrDefault(number), cancellationToken))
            .ToArray();
        var diskResults = await Task.WhenAll(tasks);
        var disks = diskResults.Where(static disk => disk is not null).Cast<StorageDisk>().ToArray();
        var diskIdsByNumber = disks
            .Where(static disk => disk.DeviceNumber.HasValue)
            .ToDictionary(static disk => disk.DeviceNumber!.Value, static disk => disk.Id);
        var resolvedVolumes = volumes.Select(volume => volume.Contract with
        {
            PhysicalDiskIds = volume.DeviceNumbers.Where(diskIdsByNumber.ContainsKey)
                .Select(number => diskIdsByNumber[number]).Distinct(StringComparer.Ordinal).ToArray()
        }).ToArray();
        return new StorageSnapshot(StorageProtocol.Major, StorageProtocol.Minor, DateTimeOffset.UtcNow,
            disks, resolvedVolumes, volumeError
                ? [new StorageDiagnostic("volume.inventory_timeout", "Volume enumeration did not complete within its time budget; physical disk collection continues.", "Windows volume APIs", DateTimeOffset.UtcNow)]
                : []);
    }

    private async Task<StorageDisk?> ReadDiskWithTimeoutAsync(int number, ReliabilityCounters? reliability,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await NativeWorkers.WaitAsync(TimeSpan.FromSeconds(7), cancellationToken))
                return UnavailableDisk(number, reliability, "device.queue_timeout", "Storage query capacity is exhausted by a non-responsive device driver.");
            var worker = Task.Run(() =>
            {
                try { return ReadDisk(number, reliability); }
                finally { NativeWorkers.Release(); }
            }, CancellationToken.None);
            try { return await worker.WaitAsync(TimeSpan.FromSeconds(7), cancellationToken); }
            catch (TimeoutException)
            {
                return UnavailableDisk(number, reliability, "device.timeout", "The storage driver did not answer within 7 seconds.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 21 or 1167) { return null; }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return UnavailableDisk(number, reliability, "device.query_failed", $"Read-only device query failed (0x{error.HResult:X8}).");
        }
    }

    private StorageDisk? ReadDisk(int number, ReliabilityCounters? reliability)
    {
        using var readHandle = NativeStorageIo.Open($@"\\.\PhysicalDrive{number}");
        var descriptorBytes = NativeStorageIo.QueryProperty(readHandle, 0);
        if (descriptorBytes is null || descriptorBytes.Length < 36) return null;
        var descriptor = ParseDescriptor(descriptorBytes);
        var capacity = NativeStorageIo.QueryCapacity(readHandle);
        var uniqueIdBytes = NativeStorageIo.QueryProperty(readHandle, 2);
        var uniqueId = ReadDeviceIdentifier(uniqueIdBytes);
        var rawIdentity = !string.IsNullOrWhiteSpace(descriptor.Serial)
            ? $"serial|{descriptor.Serial}|{descriptor.Vendor}|{descriptor.Model}"
            : uniqueId is not null ? $"device-id|{uniqueId}|{descriptor.Vendor}|{descriptor.Model}" : null;
        var confidence = rawIdentity is not null ? StorageIdentityConfidence.HardwareId : StorageIdentityConfidence.SessionOnly;
        var diskId = identities.CreateDiskId(rawIdentity ?? $"session|{Environment.MachineName}|{number}|{descriptor.Model}");
        var transport = ToTransport(descriptor.BusType);
        var technology = transport == StorageTransport.Nvme ? StorageTechnology.Nvme : StorageTechnology.Unknown;
        var rotationRate = (int?)null;
        var health = reliability?.HealthStatus switch
        {
            "critical" => StorageHealth.Critical,
            "warning" => StorageHealth.Degraded,
            "healthy" => StorageHealth.Healthy,
            _ => StorageHealth.Unknown
        };
        string? healthBasis = reliability?.HealthStatus is { } wmiHealth ? $"Windows Storage MSFT_Disk HealthStatus: {wmiHealth}." : null;
        var temperature = reliability?.TemperatureC;
        var warningTemperature = reliability?.TemperatureMaxC;
        var criticalTemperature = (int?)null;
        var wear = reliability?.WearPercent;
        var life = wear is { } wearValue ? Math.Clamp(100 - wearValue, 0, 100) : (int?)null;
        var spare = (int?)null;
        var spareThreshold = (int?)null;
        var powerHours = reliability?.PowerOnHours?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var cycles = (string?)null;
        var startStopCycles = reliability?.StartStopCycles?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var readErrors = reliability?.ReadErrorsTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var writeErrors = reliability?.WriteErrorsTotal?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var uncorrectable = Combine(reliability?.ReadErrorsUncorrected, reliability?.WriteErrorsUncorrected);
        var mediaErrors = (string?)null;
        var errorLogEntries = (string?)null;
        var readBytes = (string?)null;
        var writtenBytes = (string?)null;
        var smartSupported = (bool?)null;
        var attributes = new List<StorageSmartAttribute>();
        var diagnostics = new List<StorageDiagnostic>();

        if (transport == StorageTransport.Nvme || descriptor.BusType == 17)
        {
            var identify = NativeStorageIo.QueryNvme(readHandle, 19, 1, 1, 4096);
            var nvmeLog = NativeStorageIo.QueryNvme(readHandle, 20, 2, 2, 512);
            if (nvmeLog is not null)
            {
                var parsed = SmartParsers.ParseNvmeHealthLog(nvmeLog, identify ?? []);
                smartSupported = true;
                technology = StorageTechnology.Nvme;
                health = Worse(health, parsed.Health);
                healthBasis = Join(healthBasis, parsed.HealthBasis);
                temperature ??= parsed.TemperatureC;
                warningTemperature ??= parsed.WarningTemperatureC;
                criticalTemperature = parsed.CriticalTemperatureC;
                wear ??= parsed.WearUsedPercent;
                life ??= parsed.LifeRemainingPercent;
                spare = parsed.AvailableSparePercent;
                spareThreshold = parsed.AvailableSpareThresholdPercent;
                powerHours ??= parsed.PowerOnHours;
                cycles ??= parsed.PowerCycleCount;
                mediaErrors = parsed.MediaErrors;
                errorLogEntries = parsed.ErrorLogEntries;
                readBytes = parsed.DataReadBytes;
                writtenBytes = parsed.DataWrittenBytes;
                attributes.AddRange(parsed.Attributes);
            }
            else diagnostics.Add(Diagnostic("nvme.health_unavailable", "The storage driver did not expose the NVMe health log.", "Windows Storage protocol query"));
            if (identify is not null)
            {
                descriptor = descriptor with
                {
                    Serial = null,
                    Vendor = descriptor.Vendor,
                    Model = descriptor.Model ?? DecodeAscii(identify.AsSpan(24, 40)),
                    Firmware = descriptor.Firmware ?? DecodeAscii(identify.AsSpan(64, 8))
                };
            }
        }
        else if (transport is StorageTransport.Ata or StorageTransport.Sata or StorageTransport.Usb or StorageTransport.Raid)
        {
            try
            {
                using var commandHandle = NativeStorageIo.Open($@"\\.\PhysicalDrive{number}", allowReadOnlyCommands: true);
                var identify = NativeStorageIo.QueryAta(commandHandle, 0xEC);
                if (identify is { Length: 512 })
                {
                    var rotationWord = BinaryPrimitives.ReadUInt16LittleEndian(identify.AsSpan(217 * 2, 2));
                    if (rotationWord == 1) { technology = StorageTechnology.Ssd; rotationRate = 0; }
                    else if (rotationWord >= 0x0401) { technology = StorageTechnology.Hdd; rotationRate = rotationWord; }
                    var supported = (BinaryPrimitives.ReadUInt16LittleEndian(identify.AsSpan(82 * 2, 2)) & 1) != 0;
                    var enabled = (BinaryPrimitives.ReadUInt16LittleEndian(identify.AsSpan(85 * 2, 2)) & 1) != 0;
                    smartSupported = supported;
                    if (supported && enabled)
                    {
                        var smart = NativeStorageIo.QueryAta(commandHandle, 0xB0, 0xD0);
                        var thresholds = NativeStorageIo.QueryAta(commandHandle, 0xB0, 0xD1);
                        if (smart is { Length: 512 })
                        {
                            var parsedAttributes = SmartParsers.ParseAtaAttributes(smart, thresholds ?? []);
                            attributes.AddRange(parsedAttributes);
                            var status = NativeStorageIo.QueryAta(commandHandle, 0xB0, 0xDA, hasData: false);
                            bool? exceeded = status is { Length: >= 48 } && status[43] == 0xF4 && status[44] == 0x2C ? true
                                : status is { Length: >= 48 } && status[43] == 0x4F && status[44] == 0xC2 ? false : null;
                            var ataHealth = SmartParsers.AssessAtaHealth(supported, enabled, exceeded, parsedAttributes);
                            health = Worse(health, ataHealth);
                            healthBasis = Join(healthBasis, exceeded switch
                            {
                                true => "ATA SMART RETURN STATUS reports a threshold failure.",
                                false => "ATA SMART RETURN STATUS reports no threshold failure.",
                                _ => supported ? "ATA SMART attributes are available, but the device health status could not be confirmed." : null
                            });
                        }
                        else diagnostics.Add(Diagnostic("ata.smart_unavailable", "ATA SMART READ DATA was not exposed by the driver or bridge.", "ATA pass-through"));
                    }
                }
            }
            catch (Win32Exception error)
            {
                diagnostics.Add(Diagnostic("ata.pass_through_denied", $"Read-only ATA pass-through unavailable (0x{error.NativeErrorCode:X8}).", "ATA pass-through"));
            }
        }

        if (transport is StorageTransport.Scsi or StorageTransport.Sas or StorageTransport.Usb or StorageTransport.Raid)
        {
            try
            {
                using var commandHandle = NativeStorageIo.Open($@"\\.\PhysicalDrive{number}", allowReadOnlyCommands: true);
                var scsi = ReadScsiLogs(commandHandle);
                attributes.AddRange(scsi.Attributes);
                temperature ??= scsi.TemperatureC;
                if (scsi.Health != StorageHealth.Unknown) health = Worse(health, scsi.Health);
                healthBasis = Join(healthBasis, scsi.HealthBasis);
                diagnostics.AddRange(scsi.Diagnostics);
            }
            catch (Win32Exception error)
            {
                diagnostics.Add(Diagnostic("scsi.pass_through_denied", $"Read-only SCSI log queries unavailable (0x{error.NativeErrorCode:X8}).", "SCSI pass-through"));
            }
        }

        var seekPenalty = NativeStorageIo.QueryProperty(readHandle, 7, 16);
        if (technology == StorageTechnology.Unknown && seekPenalty is { Length: >= 9 })
            technology = seekPenalty[8] == 0 ? StorageTechnology.Ssd : StorageTechnology.Hdd;
        if (health == StorageHealth.Unknown && reliability?.HealthStatus is null && smartSupported is false)
            diagnostics.Add(Diagnostic("smart.unsupported", "No health protocol is exposed by the device driver.", "Windows Storage"));

        return new StorageDisk(diskId, confidence, number,
            descriptor.Model ?? $"Physical disk {number}",
            MetricValue(descriptor.Vendor, "STORAGE_DEVICE_DESCRIPTOR"),
            MetricValue(descriptor.Model, "STORAGE_DEVICE_DESCRIPTOR"),
            MetricValue(descriptor.Firmware, "STORAGE_DEVICE_DESCRIPTOR"),
            MetricValue(capacity, "IOCTL_DISK_GET_LENGTH_INFO"), technology, transport,
            MetricValue(rotationRate, "ATA IDENTIFY DEVICE"), health, healthBasis,
            MetricValue(temperature, "NVMe/Windows Storage reliability"),
            MetricValue(warningTemperature, "NVMe identify/Windows Storage reliability"),
            MetricValue(criticalTemperature, "NVMe identify"),
            MetricValue(wear, "NVMe/Windows Storage reliability"), MetricValue(life, "derived from a reported wear percentage"),
            MetricValue(spare, "NVMe health information log"), MetricValue(spareThreshold, "NVMe health information log"),
            MetricValue(powerHours, "NVMe/Windows Storage reliability"), MetricValue(cycles, "NVMe health information log"),
            MetricValue(startStopCycles, "Windows Storage reliability"), Metric<string>.Unsupported("ATA SMART", "Raw encoding is vendor-specific; no generic conversion is asserted."),
            Metric<string>.Unsupported("ATA SMART", "Raw encoding is vendor-specific; no generic conversion is asserted."),
            MetricValue(uncorrectable, "Windows Storage reliability"), MetricValue(readErrors, "Windows Storage reliability"),
            MetricValue(writeErrors, "Windows Storage reliability"), Metric<string>.Unavailable("Windows Storage reliability", "No standard communication-error counter was exposed."),
            MetricValue(mediaErrors, "NVMe health information log"), MetricValue(errorLogEntries, "NVMe health information log"),
            MetricValue(readBytes, "NVMe health information log"), MetricValue(writtenBytes, "NVMe health information log"),
            smartSupported switch { true => Metric<bool>.Available(true, "device protocol"), false => Metric<bool>.Available(false, "device protocol"), _ => Metric<bool>.Unavailable("device protocol") },
            attributes, diagnostics, DateTimeOffset.UtcNow);
    }

    private StorageDisk UnavailableDisk(int number, ReliabilityCounters? reliability, string code, string message)
    {
        var id = identities.CreateDiskId($"session|{Environment.MachineName}|{number}");
        return new StorageDisk(id, StorageIdentityConfidence.SessionOnly, number, $"Physical disk {number}",
            Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"),
            Metric<long>.Unavailable("device query"), StorageTechnology.Unknown, StorageTransport.Unknown,
            Metric<int>.Unavailable("device query"), reliability?.HealthStatus switch
            { "critical" => StorageHealth.Critical, "warning" => StorageHealth.Degraded, "healthy" => StorageHealth.Healthy, _ => StorageHealth.Unknown },
            null, Metric<int>.Unavailable("device query"), Metric<int>.Unavailable("device query"), Metric<int>.Unavailable("device query"),
            Metric<int>.Unavailable("device query"), Metric<int>.Unavailable("device query"), Metric<int>.Unavailable("device query"),
            Metric<int>.Unavailable("device query"), Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"),
            Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"),
            Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"),
            Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"),
            Metric<string>.Unavailable("device query"), Metric<string>.Unavailable("device query"), Metric<bool>.Unavailable("device query"),
            [], [Diagnostic(code, message, "DeviceIoControl")], DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<VolumeRead> ReadVolumes()
    {
        var volumes = new List<VolumeRead>();
        if (!OperatingSystem.IsWindows()) return volumes;
        var name = new System.Text.StringBuilder(1024);
        var search = FindFirstVolume(name, (uint)name.Capacity);
        if (search != new IntPtr(-1))
        {
            try
            {
                do { volumes.Add(ReadVolume(name.ToString())); }
                while (FindNextVolume(search, name, (uint)name.Capacity));
            }
            finally { FindVolumeClose(search); }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(static drive => drive.DriveType == DriveType.Network))
        {
            volumes.Add(new VolumeRead(identities.CreateVolumeId(drive.Name), drive.Name, null, StorageVolumeKind.Network,
                [drive.Name], null, [], null));
        }
        return volumes;
    }

    private VolumeRead ReadVolume(string volumeName)
    {
        var mountBuffer = new System.Text.StringBuilder(32768);
        var mounts = GetVolumePathNamesForVolumeName(volumeName, mountBuffer, (uint)mountBuffer.Capacity, out _)
            ? mountBuffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var root = mounts.FirstOrDefault() ?? volumeName;
        var driveType = GetDriveType(root);
        var kind = driveType switch { 2 => StorageVolumeKind.Removable, 3 => StorageVolumeKind.Local, 4 => StorageVolumeKind.Network, 5 => StorageVolumeKind.Optical, _ => StorageVolumeKind.Unknown };
        var label = new System.Text.StringBuilder(261);
        var fileSystem = new System.Text.StringBuilder(261);
        GetVolumeInformation(root, label, (uint)label.Capacity, out _, out _, out _, fileSystem, (uint)fileSystem.Capacity);
        var capacityAvailable = GetDiskFreeSpaceEx(root, out var freeToCaller, out var total, out _);
        IReadOnlyList<int> diskNumbers = [];
        try
        {
            using var handle = NativeStorageIo.Open(volumeName.TrimEnd('\\'), allowReadOnlyCommands: false);
            diskNumbers = NativeStorageIo.QueryVolumeDiskNumbers(handle);
        }
        catch (Win32Exception) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new VolumeRead(identities.CreateVolumeId(volumeName), label.ToString(), fileSystem.ToString(), kind, mounts,
            capacityAvailable ? total : null, diskNumbers,
            new VolumeStorageValue(capacityAvailable ? total : null, capacityAvailable ? freeToCaller : null),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("volume:" + volumeName.ToUpperInvariant()))).ToLowerInvariant());
    }

    private static Descriptor ParseDescriptor(byte[] bytes)
    {
        string? ReadString(uint offset)
        {
            if (offset == 0 || offset >= bytes.Length) return null;
            var end = Array.IndexOf(bytes, (byte)0, (int)offset);
            if (end < 0) end = bytes.Length;
            var text = System.Text.Encoding.ASCII.GetString(bytes, (int)offset, end - (int)offset).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return new Descriptor(ReadString(BitConverter.ToUInt32(bytes, 12)), ReadString(BitConverter.ToUInt32(bytes, 16)),
            ReadString(BitConverter.ToUInt32(bytes, 20)), ReadString(BitConverter.ToUInt32(bytes, 24)),
            BitConverter.ToUInt32(bytes, 28));
    }

    private static string? ReadDeviceIdentifier(byte[]? descriptor)
    {
        if (descriptor is not { Length: >= 28 }) return null;
        var size = Math.Min(BitConverter.ToUInt32(descriptor, 4), (uint)descriptor.Length);
        var next = 12;
        var visited = 0;
        while (next + 16 <= size && visited++ < 64)
        {
            var identifierLength = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(next + 8, 2));
            var nextOffset = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(next + 10, 2));
            if (identifierLength > 0 && next + 16 + identifierLength <= size)
                return Convert.ToHexString(descriptor.AsSpan(next + 16, identifierLength));
            if (nextOffset == 0) break;
            next += nextOffset;
        }
        return null;
    }

    private static string? DecodeAscii(ReadOnlySpan<byte> data)
    {
        var value = System.Text.Encoding.ASCII.GetString(data).Trim('\0', ' ');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static StorageTransport ToTransport(uint bus) => bus switch
    {
        1 => StorageTransport.Scsi, 3 => StorageTransport.Ata, 4 => StorageTransport.FireWire, 7 => StorageTransport.Usb,
        8 => StorageTransport.Raid, 10 => StorageTransport.Sas, 11 => StorageTransport.Sata,
        12 => StorageTransport.Sd, 13 => StorageTransport.Mmc, 14 or 15 or 16 => StorageTransport.Virtual, 17 => StorageTransport.Nvme, _ => StorageTransport.Unknown
    };

    private static ScsiSample ReadScsiLogs(SafeFileHandle handle)
    {
        var diagnostics = new List<StorageDiagnostic>();
        var attributes = new List<StorageSmartAttribute>();
        var temperature = ReadScsiLogPage(handle, 0x0D);
        int? currentTemperature = null;
        if (temperature is not null) attributes.AddRange(ParseScsiPage(temperature, out currentTemperature));
        var exceptions = ReadScsiLogPage(handle, 0x2F);
        var health = StorageHealth.Unknown;
        string? basis = null;
        if (exceptions is not null)
        {
            attributes.AddRange(ParseScsiPage(exceptions, out _));
            var asc = ReadScsiFailurePredictionAsc(exceptions);
            if (asc == 0x5D) { health = StorageHealth.Critical; basis = "SCSI Informational Exceptions log reports failure prediction (ASC 5Dh)."; }
        }
        if (temperature is null && exceptions is null)
            diagnostics.Add(Diagnostic("scsi.logs_unavailable", "The SCSI log pages were not exposed by the controller.", "SCSI LOG SENSE"));
        return new ScsiSample(health, basis, currentTemperature, attributes, diagnostics);
    }

    private static byte[]? ReadScsiLogPage(SafeFileHandle handle, byte pageCode)
    {
        var headerLength = IntPtr.Size == 8 ? 48 : 40;
        const int dataLength = 512;
        const int senseLength = 32;
        var buffer = new byte[headerLength + dataLength + senseLength];
        BitConverter.GetBytes((ushort)headerLength).CopyTo(buffer, 0);
        buffer[6] = 10; buffer[7] = senseLength; buffer[8] = 1;
        BitConverter.GetBytes((uint)dataLength).CopyTo(buffer, 12);
        BitConverter.GetBytes(5u).CopyTo(buffer, 16);
        var dataOffsetPosition = 16;
        if (IntPtr.Size == 8) BitConverter.GetBytes((ulong)headerLength).CopyTo(buffer, dataOffsetPosition);
        else BitConverter.GetBytes((uint)headerLength).CopyTo(buffer, dataOffsetPosition);
        var senseOffset = headerLength + dataLength;
        BitConverter.GetBytes((uint)senseOffset).CopyTo(buffer, IntPtr.Size == 8 ? 24 : 20);
        var cdb = IntPtr.Size == 8 ? 28 : 24;
        buffer[cdb] = 0x4D; buffer[cdb + 2] = (byte)(0x40 | (pageCode & 0x3F));
        buffer[cdb + 7] = (byte)(dataLength >> 8); buffer[cdb + 8] = (byte)(dataLength & 0xFF);
        if (!NativeStorageIo.DeviceIoControl(handle, NativeStorageIo.IoctlScsiPassThrough, buffer,
            (uint)headerLength, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero) || returned < headerLength + 4)
            return null;
        var page = buffer.AsSpan(headerLength, checked((int)returned) - headerLength).ToArray();
        if ((page[0] & 0x3F) != pageCode || page.Length < 4) return null;
        var pageLength = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(2, 2));
        if (pageLength > page.Length - 4) return null;
        Array.Resize(ref page, pageLength + 4);
        return page;
    }

    private static IReadOnlyList<StorageSmartAttribute> ParseScsiPage(byte[] page, out int? temperatureC)
    {
        temperatureC = null;
        var attributes = new List<StorageSmartAttribute>();
        for (var offset = 4; offset + 4 <= page.Length;)
        {
            var parameter = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(offset, 2));
            var length = page[offset + 3];
            if (offset + 4 + length > page.Length) break;
            var raw = Convert.ToHexString(page.AsSpan(offset + 4, length));
            if ((page[0] & 0x3F) == 0x0D && parameter == 0 && length == 1)
                temperatureC = page[offset + 4] <= 150 ? page[offset + 4] : null;
            attributes.Add(new StorageSmartAttribute("SCSI", parameter.ToString("X4"), $"SCSI log page 0x{page[0] & 0x3F:X2} parameter {parameter:X4}",
                Metric<string>.Available(raw, "SCSI LOG SENSE"), Metric<int>.Unsupported("SCSI LOG SENSE"),
                Metric<int>.Unsupported("SCSI LOG SENSE"), Metric<int>.Unsupported("SCSI LOG SENSE"),
                "SCSI parameter bytes are retained without vendor-specific decoding.", null));
            offset += 4 + length;
        }
        return attributes;
    }

    private static byte? ReadScsiFailurePredictionAsc(byte[] page)
    {
        for (var offset = 4; offset + 6 <= page.Length;)
        {
            var parameter = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(offset, 2));
            var length = page[offset + 3];
            if (offset + 4 + length > page.Length) break;
            if (parameter == 0 && length >= 2) return page[offset + 4];
            offset += 4 + length;
        }
        return null;
    }

    private static StorageHealth Worse(StorageHealth left, StorageHealth right)
    {
        static int Rank(StorageHealth value) => value switch { StorageHealth.Critical => 4, StorageHealth.Degraded => 3, StorageHealth.Unavailable => 2, StorageHealth.Healthy => 1, _ => 0 };
        return Rank(left) >= Rank(right) ? left : right;
    }

    private static string? Join(string? left, string? right) => string.IsNullOrWhiteSpace(right) ? left
        : string.IsNullOrWhiteSpace(left) ? right : left + " " + right;
    private static string? Combine(ulong? left, ulong? right) => left is null && right is null ? null : $"read:{left?.ToString() ?? "unknown"};write:{right?.ToString() ?? "unknown"}";
    private static Metric<string> MetricValue(string? value, string source) => value is null ? Metric<string>.Unavailable(source) : Metric<string>.Available(value, source);
    private static Metric<int> MetricValue(int? value, string source) => value is null ? Metric<int>.Unavailable(source) : Metric<int>.Available(value.Value, source);
    private static Metric<long> MetricValue(long? value, string source) => value is null ? Metric<long>.Unavailable(source) : Metric<long>.Available(value.Value, source);
    private static StorageDiagnostic Diagnostic(string code, string message, string source) => new(code, message, source, DateTimeOffset.UtcNow);

    private sealed record Descriptor(string? Vendor, string? Model, string? Firmware, string? Serial, uint BusType);
    private sealed record VolumeRead(string Id, string? Label, string? FileSystem, StorageVolumeKind Kind,
        IReadOnlyList<string> Mounts, long? Capacity, IReadOnlyList<int> DeviceNumbers, VolumeStorageValue? Storage,
        string? LocalMountFingerprint = null)
    {
        public StorageVolume Contract => new(Id, Label, FileSystem, Kind, Mounts,
            MetricValue(Capacity, "Windows volume inventory"),
            MetricValue(Storage?.FreeBytes, "GetDiskFreeSpaceEx"),
            MetricValue(Storage is { CapacityBytes: { } total, FreeBytes: { } free } ? Math.Max(0, total - free) : null, "GetDiskFreeSpaceEx"),
            [], Storage is null ? "Volume capacity is unavailable." : null, LocalMountFingerprint);
    }
    private sealed record VolumeStorageValue(long? CapacityBytes, long? FreeBytes);
    private sealed record ScsiSample(StorageHealth Health, string? HealthBasis, int? TemperatureC,
        IReadOnlyList<StorageSmartAttribute> Attributes, IReadOnlyList<StorageDiagnostic> Diagnostics);

    private delegate bool VolumeEnumerator(IntPtr handle, System.Text.StringBuilder volumeName, uint length);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolume(System.Text.StringBuilder volumeName, uint bufferLength);
    [DllImport("kernel32.dll", EntryPoint = "FindNextVolumeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextVolume(IntPtr handle, System.Text.StringBuilder volumeName, uint bufferLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindVolumeClose(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathNamesForVolumeName(string volumeName, System.Text.StringBuilder paths, uint bufferLength, out uint returnLength);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string rootPath);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformation(string rootPath, System.Text.StringBuilder volumeName,
        uint volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        System.Text.StringBuilder fileSystemName, uint fileSystemNameSize);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpaceEx(string directoryName, out long freeBytesAvailable,
        out long totalNumberOfBytes, out long totalNumberOfFreeBytes);
}
