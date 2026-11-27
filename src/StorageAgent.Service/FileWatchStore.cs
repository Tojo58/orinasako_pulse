using Microsoft.Data.Sqlite;
using StorageAgent.Contracts;
using StorageAgent.Core;

namespace StorageAgent.Service;

internal sealed class FileWatchStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection db;
    private const int Retention = 10_000;
    private const int PageSize = 24;
    internal record Watch(string Consumer, string Root, long Revision, bool Complete, DateTimeOffset Observed, long Cursor)
    {
        public FileWatchStatus Status => new(Root, Revision, Complete, Observed, Cursor);
    }

    public FileWatchStore(string path)
    {
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS watches(
                consumer TEXT NOT NULL, root TEXT COLLATE NOCASE NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0, complete INTEGER NOT NULL DEFAULT 0,
                observed TEXT NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00',
                cursor INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(consumer,root));
            CREATE TABLE IF NOT EXISTS entries(
                consumer TEXT NOT NULL, root TEXT COLLATE NOCASE NOT NULL, path TEXT COLLATE NOCASE NOT NULL,
                directory INTEGER NOT NULL, size INTEGER NOT NULL, modified TEXT NOT NULL, identity TEXT,
                PRIMARY KEY(consumer,root,path),
                FOREIGN KEY(consumer,root) REFERENCES watches(consumer,root) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS changes(
                consumer TEXT NOT NULL, root TEXT COLLATE NOCASE NOT NULL, sequence INTEGER NOT NULL,
                kind TEXT NOT NULL, path TEXT NOT NULL, old_path TEXT, observed TEXT NOT NULL,
                PRIMARY KEY(consumer,root,sequence),
                FOREIGN KEY(consumer,root) REFERENCES watches(consumer,root) ON DELETE CASCADE);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private List<Watch> ReadWatches(string? consumer = null, SqliteTransaction? transaction = null)
    {
        using var cmd = Command("SELECT consumer,root,revision,complete,observed,cursor FROM watches WHERE $consumer IS NULL OR consumer=$consumer ORDER BY consumer,root", transaction, ("$consumer", consumer));
        using var reader = cmd.ExecuteReader();
        var result = new List<Watch>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetBoolean(3), DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt64(5)));
        return result;
    }
    public IReadOnlyList<Watch> All() { lock (gate) return ReadWatches(); }
    public IReadOnlyList<FileWatchStatus> Status(string consumer)
    { lock (gate) return ReadWatches(consumer).Select(w => w.Status).ToArray(); }

    public void Configure(string consumer, IReadOnlyList<string> roots)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var all = ReadWatches(transaction: tx);
            if (all.Count(w => w.Consumer != consumer) + roots.Count > 64)
                throw new InvalidDataException("The machine filesystem watch limit is 64 roots.");
            var current = all.Where(w => w.Consumer == consumer).ToArray();
            foreach (var watch in current.Where(w => !roots.Contains(w.Root, StringComparer.OrdinalIgnoreCase)))
            {
                using var cmd = Command("DELETE FROM watches WHERE consumer=$c AND root=$r", tx, ("$c", consumer), ("$r", watch.Root));
                cmd.ExecuteNonQuery();
            }
            foreach (var root in roots)
            {
                using var cmd = Command("INSERT OR IGNORE INTO watches(consumer,root) VALUES($c,$r)", tx, ("$c", consumer), ("$r", root));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private Watch Find(string consumer, string root, SqliteTransaction? tx = null) =>
        ReadWatches(consumer, tx).SingleOrDefault(w => w.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The requested root is not registered by this consumer.");

    private List<FileEntry> ReadEntries(string consumer, string root, SqliteTransaction? tx = null, int? offset = null)
    {
        using var cmd = Command("SELECT path,directory,size,modified,identity FROM entries WHERE consumer=$c AND root=$r ORDER BY path" +
            (offset.HasValue ? " LIMIT $limit OFFSET $offset" : ""), tx, ("$c", consumer), ("$r", root));
        if (offset.HasValue)
        {
            cmd.Parameters.AddWithValue("$limit", PageSize + 1);
            cmd.Parameters.AddWithValue("$offset", offset.Value);
        }
        using var reader = cmd.ExecuteReader();
        var result = new List<FileEntry>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2),
            DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    public FileInventoryPage Page(string consumer, string root, long revision, int offset)
    {
        lock (gate)
        {
            var watch = Find(consumer, root);
            if (revision != watch.Revision) throw new InvalidDataException("Inventory revision changed; restart pagination.");
            var entries = ReadEntries(consumer, root, offset: offset);
            return new(watch.Status, entries.Take(PageSize).ToArray(), entries.Count > PageSize ? offset + PageSize : null);
        }
    }

    public FileChangePage Changes(string consumer, string root, long cursor)
    {
        lock (gate)
        {
            var watch = Find(consumer, root);
            if (cursor > watch.Cursor || cursor < Math.Max(0, watch.Cursor - Retention))
                return new(watch.Cursor, true, []);
            using var cmd = Command("SELECT sequence,kind,path,old_path,observed FROM changes WHERE consumer=$c AND root=$r AND sequence>$cursor ORDER BY sequence LIMIT 24",
                null, ("$c", consumer), ("$r", root), ("$cursor", cursor));
            using var reader = cmd.ExecuteReader();
            var result = new List<FileChange>();
            while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
            return new(result.Count == 0 ? watch.Cursor : result[^1].Sequence, false, result);
        }
    }

    public void Commit(Watch requested, FileInventory inventory)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var watch = ReadWatches(requested.Consumer, tx).SingleOrDefault(w => w.Root.Equals(requested.Root, StringComparison.OrdinalIgnoreCase));
            if (watch is null) return;
            var now = DateTimeOffset.UtcNow;
            var cursor = watch.Cursor;
            var revision = watch.Revision;
            // An incomplete scan is never authoritative. Keep the last complete baseline and journal.
            if (inventory.Complete)
            {
                var previous = ReadEntries(watch.Consumer, watch.Root, tx);
                var next = inventory.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
                var events = FileInventoryDiff.Compare(previous, inventory.Entries, now);
                if (events.Count != 0 || !watch.Complete || revision == 0)
                {
                    revision++;
                    using var delete = Command("DELETE FROM entries WHERE consumer=$c AND root=$r", tx, ("$c", watch.Consumer), ("$r", watch.Root));
                    delete.ExecuteNonQuery();
                    using var insert = Command("INSERT INTO entries(consumer,root,path,directory,size,modified,identity) VALUES($c,$r,$p,$d,$s,$m,$i)", tx,
                        ("$c", watch.Consumer), ("$r", watch.Root), ("$p", ""), ("$d", 0), ("$s", 0L), ("$m", ""), ("$i", null));
                    foreach (var entry in next.Values)
                    {
                        insert.Parameters["$p"].Value = entry.Path;
                        insert.Parameters["$d"].Value = entry.Directory;
                        insert.Parameters["$s"].Value = entry.SizeBytes;
                        insert.Parameters["$m"].Value = Timestamp(entry.ModifiedAt);
                        insert.Parameters["$i"].Value = (object?)entry.Identity ?? DBNull.Value;
                        insert.ExecuteNonQuery();
                    }
                    // Initial baseline is represented by the snapshot, not hundreds of thousands of created events.
                    if (watch.Revision != 0)
                    {
                        using var journal = Command("INSERT INTO changes(consumer,root,sequence,kind,path,old_path,observed) VALUES($c,$r,$s,$k,$p,$o,$t)", tx,
                            ("$c", watch.Consumer), ("$r", watch.Root), ("$s", 0L), ("$k", ""), ("$p", ""), ("$o", null), ("$t", Timestamp(now)));
                        cursor += Math.Max(0, events.Count - Retention);
                        foreach (var change in events.TakeLast(Retention))
                        {
                            journal.Parameters["$s"].Value = ++cursor;
                            journal.Parameters["$k"].Value = change.Kind;
                            journal.Parameters["$p"].Value = change.Path;
                            journal.Parameters["$o"].Value = (object?)change.OldPath ?? DBNull.Value;
                            journal.ExecuteNonQuery();
                        }
                        using var prune = Command("DELETE FROM changes WHERE consumer=$c AND root=$r AND sequence<=$min", tx,
                            ("$c", watch.Consumer), ("$r", watch.Root), ("$min", Math.Max(0, cursor - Retention)));
                        prune.ExecuteNonQuery();
                    }
                }
            }
            else if (watch.Complete || revision == 0) revision++;
            using var status = Command("UPDATE watches SET revision=$v,complete=$ok,observed=$t,cursor=$s WHERE consumer=$c AND root=$r", tx,
                ("$v", revision), ("$ok", inventory.Complete), ("$t", Timestamp(now)), ("$s", cursor), ("$c", watch.Consumer), ("$r", watch.Root));
            status.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void Dispose() { lock (gate) db.Dispose(); }
}
