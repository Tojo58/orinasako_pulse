using Orinasako.Pulse.Transport;
using System.Text.Json;
using System.Threading.Channels;
using Orinasako.Pulse.Contracts;

namespace Orinasako.Pulse.Service;

public sealed class StorageChangeHub
{
    private readonly object _gate = new();
    private readonly HashSet<Channel<StorageChange>> _subscribers = [];

    public void Publish(StorageChange change)
    {
        lock (_gate)
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(change);
    }

    public async Task<StorageChange?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<StorageChange>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (_gate) _subscribers.Add(channel);
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try { return await channel.Reader.ReadAsync(timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}

public sealed class StorageSnapshotStore(ISecretProtector protector, string? path = null)
{
    private const long MaximumSnapshotBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = StorageContractJson.Options;
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OrinasakoPulse", "snapshot.json");

    public async Task<StorageSnapshot?> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            if (new FileInfo(_path).Length is <= 0 or > MaximumSnapshotBytes) return null;
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, token);
            if (memory.Length > MaximumSnapshotBytes) return null;
            var ciphertext = memory.ToArray();
            var plaintext = protector.Unprotect(ciphertext);
            try { return JsonSerializer.Deserialize<StorageSnapshot>(plaintext, Json); }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(ciphertext);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task WriteAsync(StorageSnapshot snapshot, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        var plaintextSnapshot = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        if (plaintextSnapshot.Length > MaximumSnapshotBytes)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintextSnapshot);
            throw new InvalidDataException("Storage snapshot exceeds the configured size limit.");
        }
        var ciphertext = protector.Protect(plaintextSnapshot);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintextSnapshot);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(ciphertext, token);
                await stream.FlushAsync(token);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public sealed class StorageRuntimeState(StorageSnapshotStore store, StorageChangeHub changes)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private StorageSnapshot? _snapshot;
    private string? _lastError;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastRefreshAt;

    public StorageSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public string? LastError => Volatile.Read(ref _lastError);
    public DateTimeOffset StartedAt => _startedAt;
    public DateTimeOffset LastRefreshAt => _lastRefreshAt;

    public async Task InitializeAsync(CancellationToken token)
    {
        _startedAt = DateTimeOffset.UtcNow;
        _snapshot = await store.ReadAsync(token);
    }

    public async Task<StorageSnapshot> RefreshAsync(Orinasako.Pulse.Core.IStorageCollector collector,
        bool force, CancellationToken token)
    {
        await _refreshGate.WaitAsync(token);
        try
        {
            if (!force && Snapshot is { } cached && DateTimeOffset.UtcNow - _lastRefreshAt < TimeSpan.FromMinutes(5)) return cached;
            if (force && Snapshot is { } fresh && DateTimeOffset.UtcNow - _lastRefreshAt < TimeSpan.FromSeconds(10)) return fresh;
            var previous = Snapshot;
            var next = await collector.CollectAsync(token);
            await store.WriteAsync(next, token);
            Volatile.Write(ref _snapshot, next);
            Volatile.Write(ref _lastError, null);
            _lastRefreshAt = DateTimeOffset.UtcNow;
            PublishChanges(previous, next);
            return next;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Volatile.Write(ref _lastError, $"{error.GetType().Name}: 0x{error.HResult:X8}");
            throw;
        }
        finally { _refreshGate.Release(); }
    }

    public OrinasakoPulseStatus Status(string version) => new(version, StorageProtocol.Major, StorageProtocol.Minor,
        Snapshot is not null, StartedAt, _lastRefreshAt, LastError);

    private static IEnumerable<string> VolumeSignature(StorageSnapshot snapshot, string diskId) =>
        snapshot.Volumes.Where(volume => volume.PhysicalDiskIds.Contains(diskId, StringComparer.Ordinal))
            .OrderBy(volume => volume.Id, StringComparer.Ordinal)
            .Select(volume => JsonSerializer.Serialize(volume, StorageContractJson.Options));

    private void PublishChanges(StorageSnapshot? previous, StorageSnapshot next)
    {
        var oldById = previous?.Disks.ToDictionary(static disk => disk.Id, StringComparer.Ordinal) ?? new Dictionary<string, StorageDisk>(StringComparer.Ordinal);
        var newById = next.Disks.ToDictionary(static disk => disk.Id, StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var disk in newById.Values)
        {
            if (!oldById.TryGetValue(disk.Id, out var old))
                changes.Publish(new StorageChange(StorageChangeKind.DeviceArrived, disk.Id, null, disk.Health, now));
            else if (old.Health != disk.Health)
                changes.Publish(new StorageChange(StorageChangeKind.HealthChanged, disk.Id, old.Health, disk.Health, now));
            else if (old.TemperatureC != disk.TemperatureC || old.LifeRemainingPercent != disk.LifeRemainingPercent ||
                !VolumeSignature(previous!, disk.Id).SequenceEqual(VolumeSignature(next, disk.Id)))
                changes.Publish(new StorageChange(StorageChangeKind.MetricsChanged, disk.Id, old.Health, disk.Health, now));
        }
        foreach (var old in oldById.Values)
            if (!newById.ContainsKey(old.Id)) changes.Publish(new StorageChange(StorageChangeKind.DeviceRemoved, old.Id, old.Health, null, now));
    }
}
