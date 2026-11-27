using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StorageAgent.Contracts;

namespace StorageAgent.Core;

public sealed record UsnRecord(string Reference, string ParentReference, long Usn,
    uint Reason, uint Attributes, string Name, DateTimeOffset ObservedAt);
public sealed record UsnBuffer(long NextUsn, IReadOnlyList<UsnRecord> Records);

/// <summary>Bounded parser and projection of NTFS USN_RECORD_V2 into registered roots.</summary>
public static class UsnJournalReplay
{
    public const uint Create = 0x100, Delete = 0x200, RenameOld = 0x1000, RenameNew = 0x2000;
    private const uint Close = 0x80000000;
    public static string Reference(ulong value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"usn-reference:{value:X16}")));

    public static UsnBuffer Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8) throw new InvalidDataException("USN cursor missing.");
        var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var records = new List<UsnRecord>();
        for (var offset = 8; offset < buffer.Length;)
        {
            var remaining = buffer[offset..];
            if (remaining.Length < 60) throw new InvalidDataException("USN record truncated.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            var major = BinaryPrimitives.ReadUInt16LittleEndian(remaining[4..]);
            if (major != 2 || length < 60 || length > remaining.Length || (length & 7) != 0)
                throw new InvalidDataException("Unsupported or malformed USN record.");
            var record = remaining[..(int)length];
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            if (nameOffset < 60 || (nameLength & 1) != 0 || nameOffset + nameLength > record.Length)
                throw new InvalidDataException("USN filename outside record.");
            DateTimeOffset observed;
            try { observed = DateTimeOffset.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(record[32..])); }
            catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Invalid USN timestamp."); }
            records.Add(new(Reference(BinaryPrimitives.ReadUInt64LittleEndian(record[8..])),
                Reference(BinaryPrimitives.ReadUInt64LittleEndian(record[16..])),
                BinaryPrimitives.ReadInt64LittleEndian(record[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[52..]),
                Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength)), observed));
            offset += (int)length;
        }
        return new(next, records);
    }

    public static IReadOnlyList<FileChange> Project(string root, string rootReference,
        IReadOnlyList<FileEntry> previous, IReadOnlyList<UsnRecord> records)
    {
        var directories = previous.Where(e => e.Directory && e.JournalReference is not null)
            .GroupBy(e => e.JournalReference!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Path, StringComparer.Ordinal);
        directories[rootReference] = root;
        var pending = new Dictionary<string, (string Path, DateTimeOffset Time)>(StringComparer.Ordinal);
        var changes = new List<FileChange>();
        static bool Within(string parent, string child) =>
            child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        void RemoveDirectory(string path)
        {
            foreach (var key in directories.Where(x => x.Value.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                Within(path, x.Value)).Select(x => x.Key).ToArray()) directories.Remove(key);
        }
        void MoveDirectory(string oldPath, string newPath)
        {
            foreach (var key in directories.Where(x => x.Value.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                Within(oldPath, x.Value)).Select(x => x.Key).ToArray())
                directories[key] = newPath + directories[key][oldPath.Length..];
        }
        void Emit(string kind, string path, string? oldPath, DateTimeOffset time) =>
            changes.Add(new(0, kind, path, oldPath, time, "usn"));

        foreach (var record in records)
        {
            if ((record.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.System)) != 0) continue;
            var isDirectory = (record.Attributes & (uint)FileAttributes.Directory) != 0;
            string? path = null;
            if (directories.TryGetValue(record.ParentReference, out var parent) &&
                record.Name.Length is > 0 and <= 255 && record.Name is not ("." or "..") &&
                record.Name.IndexOfAny(['\\', '/', ':', '\0']) < 0)
            {
                var candidate = Path.Combine(parent, record.Name);
                if (Within(root, candidate) && !FileSystemInventory.IsExcludedTopLevel(root, parent, record.Name))
                    path = candidate;
            }
            if ((record.Reason & RenameOld) != 0)
            {
                if (path is not null) pending[record.Reference] = (path, record.ObservedAt);
                continue;
            }
            if ((record.Reason & RenameNew) != 0)
            {
                if (pending.Remove(record.Reference, out var old))
                {
                    if (path is not null)
                    {
                        var kind = string.Equals(Path.GetDirectoryName(old.Path), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)
                            ? "renamed" : "moved";
                        Emit(kind, path, old.Path, record.ObservedAt);
                        if (isDirectory) MoveDirectory(old.Path, path);
                    }
                    else
                    {
                        Emit("deleted", old.Path, null, record.ObservedAt);
                        if (isDirectory) RemoveDirectory(old.Path);
                    }
                }
                else if (path is not null) Emit("created", path, null, record.ObservedAt);
                continue;
            }
            if (path is null) continue;
            if ((record.Reason & Create) != 0) Emit("created", path, null, record.ObservedAt);
            if ((record.Reason & Delete) != 0)
            {
                Emit("deleted", path, null, record.ObservedAt);
                if (isDirectory) RemoveDirectory(path);
            }
            else if ((record.Reason & ~(Close | Create)) != 0 && (record.Reason & Create) == 0)
                Emit("modified", path, null, record.ObservedAt);
        }
        foreach (var old in pending.Values) Emit("deleted", old.Path, null, old.Time);
        return changes;
    }
}
