using System.Diagnostics;
using System.Text.Json;
using StorageAgent.Contracts;
using StorageAgent.Core;

namespace StorageAgent.Service;

internal static class FileInventoryWorker
{
    internal static async Task<FileInventory> ScanAsync(string root, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "StorageAgent.Service.exe");
        var output = Path.GetTempFileName();
        Process? child = null;
        try
        {
            // Use the active user's permissions even for local paths. Never fall back to SYSTEM.
            child = InteractiveUserProcess.TryStart(executable, ["--filesystem-inventory", root, output], output)
                ?? throw new IOException("No active Windows session for filesystem inventory.");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(3));
            await child.WaitForExitAsync(budget.Token);
            if (child.ExitCode != 0 || new FileInfo(output).Length > 128L * 1024 * 1024)
                throw new IOException("Inventory worker failed or exceeded its output budget.");
            await using var input = File.OpenRead(output);
            var result = await JsonSerializer.DeserializeAsync<FileInventory>(input, StorageContractJson.Options, budget.Token)
                ?? throw new InvalidDataException("Inventory worker returned no data.");
            if (!result.Root.Equals(root, StringComparison.OrdinalIgnoreCase) || result.Entries.Count > FileSystemInventory.MaxEntries ||
                result.Entries.Any(entry => !IsWithin(root, entry.Path)))
                throw new InvalidDataException("Inventory worker returned invalid paths.");
            return result;
        }
        finally
        {
            if (child is not null)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                child.Dispose();
            }
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    private static bool IsWithin(string root, string path)
    {
        try
        {
            var normalized = FileSystemInventory.Normalize(path);
            return normalized.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
}
