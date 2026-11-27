using StorageAgent.Contracts;
using StorageAgent.Core;
using StorageAgent.Service;

namespace StorageAgent.Tests;

public sealed class FileInventoryTests
{
    [Fact]
    public void ReconciliationPreservesRenameAndMoveWhenIdentityIsUnique()
    {
        var now = DateTimeOffset.UtcNow;
        FileEntry before = new(Path.Combine("root", "before.mkv"), false, 1, now, "identity");
        var after = before with { Path = Path.Combine("root", "after.mkv") };
        var change = Assert.Single(FileInventoryDiff.Compare([before], [after], now));
        Assert.Equal("renamed", change.Kind);
        Assert.Equal(before.Path, change.OldPath);
        var moved = after with { Path = Path.Combine("root", "other", "after.mkv") };
        Assert.Equal("moved", Assert.Single(FileInventoryDiff.Compare([after], [moved], now)).Kind);
        Assert.Equal("deleted", Assert.Single(FileInventoryDiff.Compare([before], [], now)).Kind);
    }

    [Fact]
    public void IncompleteInventoryCannotDeleteBaselineAndJournalSurvivesReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "journal.sqlite");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var root = Path.Combine(directory, "root");
            var file = new FileEntry(Path.Combine(root, "file"), false, 1, now, "a");
            using (var store = new FileWatchStore(database))
            {
                store.Configure("consumer", [root]);
                store.Commit(Assert.Single(store.All()), new(root, true, [file], []));
                store.Commit(Assert.Single(store.All()), new(root, false, [], [root]));
                var status = Assert.Single(store.Status("consumer"));
                Assert.False(status.Complete);
                Assert.Single(store.Page("consumer", root, status.Revision, 0).Entries);
                Assert.Empty(store.Changes("consumer", root, 0).Changes);
                store.Commit(Assert.Single(store.All()), new(root, true, [], []));
            }
            using (var store = new FileWatchStore(database))
            {
                Assert.Equal("deleted", Assert.Single(store.Changes("consumer", root, 0).Changes).Kind);
                Assert.Throws<InvalidDataException>(() => store.Page("other", root, 1, 0));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ChangedWindowsPrincipalCannotDeleteAnotherUsersInventory()
    {
        using var store = new FileWatchStore(":memory:");
        var root = Path.Combine(Path.GetTempPath(), "protected");
        var file = new FileEntry(Path.Combine(root, "movie"), false, 1, DateTimeOffset.UtcNow);
        store.Configure("consumer", [root]);
        store.Commit(Assert.Single(store.All()), new(root, true, [file], [], "principal-A"));
        store.Commit(Assert.Single(store.All()), new(root, true, [], [], "principal-B"));
        var status = Assert.Single(store.Status("consumer"));
        Assert.False(status.Complete);
        Assert.True(status.Offline);
        Assert.Single(store.Page("consumer", root, status.Revision, 0).Entries);
        Assert.Empty(store.Changes("consumer", root, 0).Changes);
    }

    [Fact]
    public void FilesystemIpcIsAdministrativeAndNotRemote()
    {
        Assert.True(StorageProtocol.RequiresAdministrator(StorageOperation.FileWatchConfigure));
        Assert.True(StorageProtocol.RequiresAdministrator(StorageOperation.FileInventoryPage));
        Assert.True(StorageProtocol.RequiresAdministrator(StorageOperation.FileChanges));
        Assert.DoesNotContain(Enum.GetNames<RemoteCommandKind>(), name => name.Contains("File", StringComparison.OrdinalIgnoreCase));
    }
}
