using StorageAgent.Contracts;

namespace StorageAgent.Core;

public static class FileInventoryDiff
{
    public static IReadOnlyList<FileChange> Compare(IReadOnlyList<FileEntry> previous, IReadOnlyList<FileEntry> current, DateTimeOffset now)
    {
        var old = previous.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var next = current.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var removed = old.Values.Where(e => !next.ContainsKey(e.Path)).ToList();
        var added = next.Values.Where(e => !old.ContainsKey(e.Path)).ToList();
        var result = new List<FileChange>();
        var oldUnique = previous.Where(e => e.Identity is not null).GroupBy(e => e.Identity!)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var newUnique = current.Where(e => e.Identity is not null).GroupBy(e => e.Identity!)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        foreach (var entry in removed.ToArray())
        {
            if (entry.Identity is null || !oldUnique.ContainsKey(entry.Identity) ||
                !newUnique.TryGetValue(entry.Identity, out var replacement) || !added.Contains(replacement) ||
                entry.Directory != replacement.Directory) continue;
            var kind = string.Equals(Path.GetDirectoryName(entry.Path), Path.GetDirectoryName(replacement.Path), StringComparison.OrdinalIgnoreCase)
                ? "renamed" : "moved";
            result.Add(new(0, kind, replacement.Path, entry.Path, now));
            removed.Remove(entry); added.Remove(replacement);
        }
        result.AddRange(removed.Select(e => new FileChange(0, "deleted", e.Path, null, now)));
        result.AddRange(added.Select(e => new FileChange(0, "created", e.Path, null, now)));
        foreach (var entry in next.Values)
        {
            if (!old.TryGetValue(entry.Path, out var before)) continue;
            if (!string.Equals(entry.Path, before.Path, StringComparison.Ordinal))
                result.Add(new(0, "renamed", entry.Path, before.Path, now));
            else if (entry != before)
                result.Add(new(0, "modified", entry.Path, null, now));
        }
        return result;
    }
}
