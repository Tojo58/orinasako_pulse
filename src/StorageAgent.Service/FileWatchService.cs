using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StorageAgent.Contracts;
using StorageAgent.Core;

namespace StorageAgent.Service;

/// <summary>Local administrative subscriptions, durable snapshot and event journal.</summary>
internal sealed class FileWatchService : BackgroundService
{
    private sealed class Observation : IDisposable
    {
        public FileSystemWatcher? Watcher;
        public DateTimeOffset LastScan;
        public string? Usn;
        public int Dirty = 1;
        public void Dispose() => Watcher?.Dispose();
    }
    private readonly FileWatchStore store;
    private readonly ILogger<FileWatchService> logger;
    private readonly ConcurrentDictionary<(string Consumer, string Root), Observation> observations = new();

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
        return true;
    }
    public IReadOnlyList<FileWatchStatus> Status(FileWatchRequest? request) => store.Status(Consumer(request));
    public FileInventoryPage Page(FileWatchRequest? request)
    {
        if (request is null || request.Offset is < 0 or > FileSystemInventory.MaxEntries || request.Revision < 0)
            throw new InvalidDataException("Invalid inventory cursor.");
        return store.Page(Consumer(request), Root(request), request.Revision, request.Offset);
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

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var watches = store.All();
            var keys = watches.Select(w => (w.Consumer, w.Root)).ToHashSet();
            foreach (var key in observations.Keys)
                if (!keys.Contains(key) && observations.TryRemove(key, out var old)) old.Dispose();
            var due = new List<(FileWatchStore.Watch Watch, Observation Observation)>();
            foreach (var watch in watches)
            {
                var observation = observations.GetOrAdd((watch.Consumer, watch.Root), _ => new());
                if (observation.Watcher is null && !FileSystemInventory.IsNetwork(watch.Root))
                    Attach(watch.Root, observation);
                // USN detects lost watcher notifications and journal replacement on NTFS.
                // Non-NTFS/SMB and restart always use a bounded full reconciliation.
                var usn = FileSystemInventory.IsNetwork(watch.Root) ? null : UsnCheckpoint.Read(watch.Root);
                if (usn is not null && observation.Usn is not null && usn != observation.Usn)
                    Interlocked.Exchange(ref observation.Dirty, 1);
                observation.Usn = usn;
                if (Interlocked.Exchange(ref observation.Dirty, 0) != 0 ||
                    DateTimeOffset.UtcNow - observation.LastScan >= TimeSpan.FromMinutes(1))
                    due.Add((watch, observation));
            }
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                async (item, ct) =>
                {
                    FileInventory inventory;
                    try { inventory = await FileInventoryWorker.ScanAsync(item.Watch.Root, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        logger.LogWarning("Filesystem inventory unavailable ({Type}).", error.GetType().Name);
                        inventory = new(item.Watch.Root, false, [], [item.Watch.Root]);
                    }
                    try { store.Commit(item.Watch, inventory); }
                    catch (Exception error) { logger.LogWarning("Filesystem journal commit failed ({Type}).", error.GetType().Name); }
                    item.Observation.LastScan = DateTimeOffset.UtcNow;
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
            watcher.Error += (_, _) => Dirty();
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
