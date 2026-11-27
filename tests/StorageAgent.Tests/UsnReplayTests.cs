using System.Buffers.Binary;
using System.Text;
using StorageAgent.Core;
using StorageAgent.Service;

namespace StorageAgent.Tests;

public sealed class UsnReplayTests
{
    [Fact]
    public void ParsesNativeV2RecordAndRejectsMalformedLength()
    {
        var bytes = new byte[8 + 64];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 120);
        var record = bytes.AsSpan(8);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 64);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], 11);
        BinaryPrimitives.WriteUInt64LittleEndian(record[16..], 12);
        BinaryPrimitives.WriteInt64LittleEndian(record[24..], 100);
        BinaryPrimitives.WriteInt64LittleEndian(record[32..], DateTimeOffset.UtcNow.ToFileTime());
        BinaryPrimitives.WriteUInt32LittleEndian(record[40..], UsnJournalReplay.Create);
        BinaryPrimitives.WriteUInt16LittleEndian(record[56..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record[58..], 60);
        Encoding.Unicode.GetBytes("x").AsSpan().CopyTo(record[60..]);
        var result = UsnJournalReplay.Parse(bytes);
        Assert.Equal(120L, result.NextUsn);
        var parsed = Assert.Single(result.Records);
        Assert.Equal("x", parsed.Name);
        Assert.Equal(UsnJournalReplay.Reference(12), parsed.ParentReference);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1000);
        Assert.Throws<InvalidDataException>(() => UsnJournalReplay.Parse(bytes));
    }

    [Fact]
    public void ReplaysTransientFilesAndRenamesWithoutResolvingOutsideRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "authorized");
        var time = DateTimeOffset.UtcNow;
        var events = UsnJournalReplay.Project(root, "root", [], [
            new("f", "root", 1, UsnJournalReplay.Create, 0, "transient", time),
            new("f", "root", 2, UsnJournalReplay.Delete, 0, "transient", time),
            new("r", "root", 3, UsnJournalReplay.RenameOld, 0, "old", time),
            new("r", "root", 4, UsnJournalReplay.RenameNew, 0, "new", time),
            new("outside", "unknown", 5, UsnJournalReplay.Create, 0, "private", time),
            new("unsafe", "root", 6, UsnJournalReplay.Create, 0, "..", time)
        ]);
        Assert.Equal(new[] { "created", "deleted", "renamed" }, events.Select(e => e.Kind));
        Assert.Equal(Path.Combine(root, "old"), events[2].OldPath);
        Assert.All(events, change => Assert.Equal("usn", change.Source));
    }

    [Fact]
    public void JournalGapForcesSnapshotResetAndCheckpointPersists()
    {
        using var store = new FileWatchStore(":memory:");
        var root = Path.Combine(Path.GetTempPath(), "usn-root");
        store.Configure("consumer", [root]);
        store.Commit(Assert.Single(store.All()), new(root, true, [], [], RootReference: "root"),
            new("01:10", false, []));
        store.Commit(Assert.Single(store.All()), new(root, true, [], [], RootReference: "root"),
            new("01:20", false, [new("file", "root", 11, UsnJournalReplay.Create, 0, "transient", DateTimeOffset.UtcNow)]));
        Assert.Equal("01:20", Assert.Single(store.All()).UsnPosition);
        Assert.Equal("usn", Assert.Single(store.Changes("consumer", root, 0).Changes).Source);
        store.Commit(Assert.Single(store.All()), new(root, true, [], [], RootReference: "root"), new("02:30", true, []));
        Assert.True(store.Changes("consumer", root, 1).ResetRequired);
        Assert.Equal("02:30", Assert.Single(store.All()).UsnPosition);
    }
}
