using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Orinasako.Pulse.Contracts;

namespace Orinasako.Pulse.Core;

/// <summary>Generic, read-only, bounded inventory. Does not follow reparse points.</summary>
public static class FileSystemInventory
{
    public const int MaxEntries = 250_000;
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        RecurseSubdirectories = false, ReturnSpecialDirectories = false
    };
    private static readonly HashSet<string> SystemDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "System Volume Information", "$RECYCLE.BIN", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs", "$WinREAgent" };

    public static string Normalize(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Length > 4096 || !Path.IsPathFullyQualified(root) ||
            root.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("An ordinary absolute filesystem path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public static bool IsNetwork(string root) => root.StartsWith(@"\\", StringComparison.Ordinal);

    public static FileInventory Scan(string root, CancellationToken token = default)
    {
        root = Normalize(root);
        var entries = new List<FileEntry>();
        var inaccessible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        try
        {
            // A configured root must not escape through a junction/symlink.
            for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    return new(root, false, [], [root]);
            if (!Directory.Exists(root)) return new(root, false, [], [root]);
        }
        catch (Exception error) when (AccessFailure(error)) { return new(root, false, [], [root]); }
        var rootIdentity = OperatingSystem.IsWindows() && !IsNetwork(root) ? Identity(root) : default;
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                { inaccessible.Add(directory); continue; }
                foreach (var child in Directory.EnumerateFileSystemEntries(directory, "*", Options))
                {
                    token.ThrowIfCancellationRequested();
                    if (entries.Count >= MaxEntries) return new(root, false, entries, [root]);
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if (isDirectory && IsExcludedTopLevel(root, directory, Path.GetFileName(child))) continue;
                        var info = new FileInfo(child);
                        var identity = OperatingSystem.IsWindows() && !IsNetwork(root) ? Identity(child) : default;
                        entries.Add(new(child, isDirectory, isDirectory ? 0 : info.Length,
                            info.LastWriteTimeUtc, identity.Hash, identity.Reference));
                        if (isDirectory) pending.Push(child);
                    }
                    catch (Exception error) when (AccessFailure(error)) { inaccessible.Add(directory); }
                }
            }
            catch (Exception error) when (AccessFailure(error)) { inaccessible.Add(directory); }
        }
        return new(root, inaccessible.Count == 0, entries, inaccessible.Take(1024).ToArray(),
            RootIdentity: rootIdentity.Hash, RootReference: rootIdentity.Reference);
    }

    public static bool IsExcludedTopLevel(string root, string parent, string name) =>
        parent.Equals(root, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) && SystemDirectories.Contains(name);

    public static IReadOnlyList<string> Directories(string root)
    {
        root = Normalize(root);
        return Directory.EnumerateDirectories(root, "*", Options).Take(300)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static bool Probe(string root)
    {
        root = Normalize(root);
        if (!Directory.Exists(root)) return false;
        Directory.EnumerateFileSystemEntries(root, "*", Options).Take(1).ToArray();
        return true;
    }

    private static bool AccessFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;

    // An identity is only a best-effort rename correlation, never an authorization token.
    private static (string? Hash, string? Reference) Identity(string path)
    {
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return default;
        var input = $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}:{info.FileIndexLow:X8}:{info.CreationHigh:X8}:{info.CreationLow:X8}";
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))),
            UsnJournalReplay.Reference(((ulong)info.FileIndexHigh << 32) | info.FileIndexLow));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeInfo info);
}
