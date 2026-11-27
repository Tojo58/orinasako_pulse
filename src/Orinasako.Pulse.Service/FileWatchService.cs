using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orinasako.Pulse.Contracts;
using Orinasako.Pulse.Core;

namespace Orinasako.Pulse.Service;

/// <summary>Local administrative subscriptions, durable snapshot and event journal.</summary>
internal sealed class FileWatchService : BackgroundService
{
    private sealed class Observation : IDisposable
    {
        public readonly object Gate = new();
        public FileSystemWatcher? Watcher;
        public CancellationTokenSource? Worker;
        public int Reattach;
        public DateTimeOffset LastScan;
        public string? Usn;
        public DateTimeOffset LastUsnCheck;
        public int Scanning;
        public int Dirty = 1;
        public void Dispose()
        {
            lock (Gate)
            {
                Worker?.Cancel();
                Watcher?.Dispose();
                Watcher = null;
            }
        }
    }
    private readonly FileWatchStore store;
    private readonly ILogger<FileWatchService> logger;
    private readonly ConcurrentDictionary<(string Consumer, string Root), Observation> observations = new();

    private readonly ConcurrentDictionary<string, DateTimeOffset> pausedVolumes = new(StringComparer.OrdinalIgnoreCase);

    public FileWatchService(ILogger<FileWatchService> logger)
    {
        this.logger = logger;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StorageAgent");
        Directory.CreateDirectory(directory);
        store = new(Path.Combine(directory, "filesystem.sqlite"));
    }

    private static string Consumer(FileWatchRequest? request)
    {
        if (request?.Consumer is not { } consumer || !Regex.IsMatch(consumer, "^[a-zA-Z0-9_.-]{1,64}$"))
            throw new InvalidDataException("Invalid filesystem consumer.");
        return consumer;
    }

    private static string Root(FileWatchRequest? request)
    {
        try { return FileSystemInventory.Normalize(request?.Root ?? ""); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid filesystem root."); }
    }

    public bool Configure(FileWatchRequest? request)
    {
        var consumer = Consumer(request);
        if (request?.Roots is null || request.Roots.Count > 32)
            throw new InvalidDataException("At most 32 roots may be registered per consumer.");
        string[] roots;
        try { roots = request.Roots.Select(FileSystemInventory.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid filesystem root."); }
        store.Configure(consumer, roots);
        foreach (var key in observations.Keys.Where(key => key.Consumer == consumer && !roots.Contains(key.Root, StringComparer.OrdinalIgnoreCase)))
            if (observations.TryRemove(key, out var removed)) removed.Dispose();
        return true;
    }
    public IReadOnlyList<FileWatchStatus> Status(FileWatchRequest? request)
    {
        var consumer = Consumer(request);
        return store.Status(consumer).Select(status => Visible(consumer, status)).ToArray();
    }

    private FileWatchStatus Visible(string consumer, FileWatchStatus status)
    {
        if (!observations.TryGetValue((consumer, status.Root), out var observation) ||
            Volatile.Read(ref observation.Dirty) != 0 || Volatile.Read(ref observation.Scanning) != 0)
            return status with { Complete = false };
        return status;
    }
    public FileInventoryPage Page(FileWatchRequest? request)
    {
        if (request is null || request.Offset is < 0 or > FileSystemInventory.MaxEntries || request.Revision < 0)
            throw new InvalidDataException("Invalid inventory cursor.");
        var consumer = Consumer(request);
        var page = store.Page(consumer, Root(request), request.Revision, request.Offset);
        return page with { Status = Visible(consumer, page.Status) };
    }
    public FileChangePage Changes(FileWatchRequest? request)
    {
        if (request is null || request.Cursor < 0) throw new InvalidDataException("Invalid journal cursor.");
        return store.Changes(Consumer(request), Root(request), request.Cursor);
    }
    public bool Refresh(FileWatchRequest? request)
    {
        var consumer = Consumer(request);
        var root = Root(request);
        var watch = store.All().SingleOrDefault(w => w.Consumer == consumer && w.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Filesystem root is not registered.");
        if (observations.TryGetValue((watch.Consumer, watch.Root), out var observation))
            Interlocked.Exchange(ref observation.Dirty, 1);
        return true;
    }

    private bool Paused(string root)
    {
        var volume = Path.GetPathRoot(root);
        return volume is not null && pausedVolumes.TryGetValue(volume, out var until) && until > DateTimeOffset.UtcNow;
    }

    public async Task<bool> PauseVolumeAsync(FileWatchRequest? request, CancellationToken token)
    {
        _ = Consumer(request);
        var root = Root(request);
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != Path.DirectorySeparatorChar)
            throw new InvalidDataException("A drive root is required for a removal pause.");
        pausedVolumes[root] = DateTimeOffset.UtcNow.AddSeconds(60);
        var affected = observations.Where(pair => string.Equals(Path.GetPathRoot(pair.Key.Root), root, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value).ToArray();
        foreach (var observation in affected)
        {
            lock (observation.Gate)
            {
                Interlocked.Exchange(ref observation.Dirty, 1);
                observation.Worker?.Cancel();
                observation.Watcher?.Dispose();
                observation.Watcher = null;
            }
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        while (affected.Any(observation => { lock (observation.Gate) return observation.Worker is not null; }))
            await Task.Delay(100, budget.Token);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            foreach (var pause in pausedVolumes.Where(pair => pair.Value <= DateTimeOffset.UtcNow).ToArray())
                pausedVolumes.TryRemove(pause.Key, out _);
            var watches = store.All();
            var keys = watches.Select(w => (w.Consumer, w.Root)).ToHashSet();
            foreach (var key in observations.Keys)
                if (!keys.Contains(key) && observations.TryRemove(key, out var old)) old.Dispose();
            var due = new List<(FileWatchStore.Watch Watch, Observation Observation)>();
            foreach (var watch in watches)
            {
                var observation = observations.GetOrAdd((watch.Consumer, watch.Root), _ => new());
                if (Paused(watch.Root)) continue;
                lock (observation.Gate)
                {
                    if (Interlocked.Exchange(ref observation.Reattach, 0) != 0)
                    {
                        observation.Watcher?.Dispose();
                        observation.Watcher = null;
                    }
                    if (!Paused(watch.Root) && observation.Watcher is null && !FileSystemInventory.IsNetwork(watch.Root))
                        Attach(watch.Root, observation);
                }
                // USN detects lost watcher notifications and journal replacement on NTFS.
                // Non-NTFS/SMB and restart always use a bounded full reconciliation.
                if (DateTimeOffset.UtcNow - observation.LastUsnCheck >= TimeSpan.FromSeconds(30))
                {
                    var usn = FileSystemInventory.IsNetwork(watch.Root) ? null : UsnCheckpoint.Read(watch.Root);
                    if (usn is not null && observation.Usn is not null && usn != observation.Usn)
                        Interlocked.Exchange(ref observation.Dirty, 1);
                    observation.Usn = usn;
                    observation.LastUsnCheck = DateTimeOffset.UtcNow;
                }
                if (Interlocked.Exchange(ref observation.Dirty, 0) != 0 ||
                    DateTimeOffset.UtcNow - observation.LastScan >= TimeSpan.FromMinutes(1))
                {
                    Volatile.Write(ref observation.Scanning, 1);
                    due.Add((watch, observation));
                }
            }
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                async (item, ct) =>
                {
                    using var worker = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    lock (item.Observation.Gate)
                    {
                        if (Paused(item.Watch.Root))
                        {
                            Interlocked.Exchange(ref item.Observation.Dirty, 1);
                            Volatile.Write(ref item.Observation.Scanning, 0);
                            return;
                        }
                        item.Observation.Worker = worker;
                    }
                    try
                    {
                    var upper = UsnCheckpoint.Read(item.Watch.Root);
                    FileInventory inventory;
                    try { inventory = await FileInventoryWorker.ScanAsync(item.Watch.Root, worker.Token); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (worker.IsCancellationRequested)
                    { Interlocked.Exchange(ref item.Observation.Dirty, 1); return; }
                    catch (Exception error)
                    {
                        logger.LogWarning("Filesystem inventory unavailable ({Type}).", error.GetType().Name);
                        inventory = new(item.Watch.Root, false, [], [item.Watch.Root]);
                    }
                    if (worker.IsCancellationRequested) { Interlocked.Exchange(ref item.Observation.Dirty, 1); return; }
                    try
                    {
                        var recovery = inventory.Complete ? UsnCheckpoint.Recover(item.Watch.Root, item.Watch.UsnPosition, upper) : null;
                        store.Commit(item.Watch, inventory, recovery);
                    }
                    catch (Exception error)
                    {
                        logger.LogWarning("Filesystem journal commit failed ({Type}).", error.GetType().Name);
                        try { store.Commit(item.Watch, new(item.Watch.Root, false, [], [item.Watch.Root])); }
                        catch (Exception) { Interlocked.Exchange(ref item.Observation.Dirty, 1); }
                    }
                    item.Observation.LastScan = DateTimeOffset.UtcNow;
                    }
                    finally
                    {
                        lock (item.Observation.Gate) item.Observation.Worker = null;
                        Volatile.Write(ref item.Observation.Scanning, 0);
                    }
                });
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    private static void Attach(string root, Observation observation)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            if (!Directory.Exists(root)) return;
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            void Dirty() => Interlocked.Exchange(ref observation.Dirty, 1);
            watcher.Created += (_, _) => Dirty(); watcher.Deleted += (_, _) => Dirty();
            watcher.Changed += (_, _) => Dirty(); watcher.Renamed += (_, _) => Dirty();
            watcher.Error += (_, _) => { Interlocked.Exchange(ref observation.Reattach, 1); Dirty(); };
            watcher.EnableRaisingEvents = true;
            observation.Watcher = watcher;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { watcher?.Dispose(); }
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var item in observations.Values) item.Dispose();
        store.Dispose();
    }
}
