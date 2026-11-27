namespace StorageAgent.Contracts;

// Local IPC only. Never included in remote storage/system telemetry.
public sealed record FileWatchRequest(string Consumer, IReadOnlyList<string>? Roots = null,
    string? Root = null, long Revision = 0, int Offset = 0, long Cursor = 0);
public sealed record FileEntry(string Path, bool Directory, long SizeBytes, DateTimeOffset ModifiedAt, string? Identity = null);
public sealed record FileInventory(string Root, bool Complete, IReadOnlyList<FileEntry> Entries, IReadOnlyList<string> Inaccessible, string? PrincipalKey = null);
public sealed record FileWatchStatus(string Root, long Revision, bool Complete, DateTimeOffset ObservedAt, long Cursor, bool Offline = false);
public sealed record FileInventoryPage(FileWatchStatus Status, IReadOnlyList<FileEntry> Entries, int? NextOffset);
public sealed record FileChange(long Sequence, string Kind, string Path, string? OldPath, DateTimeOffset ObservedAt);
public sealed record FileChangePage(long Cursor, bool ResetRequired, IReadOnlyList<FileChange> Changes);
