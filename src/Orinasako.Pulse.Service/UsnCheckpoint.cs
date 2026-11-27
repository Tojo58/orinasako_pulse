using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Orinasako.Pulse.Core;

namespace Orinasako.Pulse.Service;

internal sealed record UsnRecovery(string? Position, bool Gap, IReadOnlyList<UsnRecord> Records);

/// <summary>Read-only NTFS USN replay with bounded record count and explicit gap detection.</summary>
internal static class UsnCheckpoint
{
    private const uint QueryJournal = 0x000900F4, ReadJournal = 0x000900BB;
    private const int MaxRecords = 20_000;
    private sealed record Position(ulong Id, long Next);
    private sealed record Journal(ulong Id, long First, long Next);
    public static string? Read(string root)
    {
        if (!OperatingSystem.IsWindows() || FileSystemInventory.IsNetwork(root)) return null;
        using var handle = Open(root);
        if (handle is null) return null;
        var journal = Query(handle);
        return journal is null ? null : $"{journal.Id:X16}:{journal.Next.ToString(CultureInfo.InvariantCulture)}";
    }

    public static UsnRecovery Recover(string root, string? previous, string? upper)
    {
        if (upper is null) return new(previous, false, []);
        if (previous is null) return new(upper, false, []);
        if (!TryParse(previous, out var start) || !TryParse(upper, out var end) || start.Id != end.Id || start.Next > end.Next)
            return new(upper, true, []);
        if (start.Next == end.Next) return new(upper, false, []);
        using var handle = Open(root);
        if (handle is null) return new(upper, true, []);
        var journal = Query(handle);
        if (journal is null || journal.Id != start.Id || start.Next < journal.First || end.Next > journal.Next)
            return new(upper, true, []);
        var input = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), start.Id);
        var output = new byte[256 * 1024];
        var records = new List<UsnRecord>();
        var cursor = start.Next;
        try
        {
            for (var page = 0; cursor < end.Next && page < 128; page++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(input, cursor);
                if (!DeviceIoControl(handle, ReadJournal, input, (uint)input.Length, output, (uint)output.Length, out var size, IntPtr.Zero) ||
                    size < 8 || size > output.Length) return new(upper, true, []);
                var parsed = UsnJournalReplay.Parse(output.AsSpan(0, (int)size));
                if (parsed.NextUsn <= cursor) return new(upper, true, []);
                records.AddRange(parsed.Records.Where(record => record.Usn >= cursor && record.Usn < end.Next));
                if (records.Count > MaxRecords) return new(upper, true, []);
                cursor = parsed.NextUsn;
            }
        }
        catch (InvalidDataException) { return new(upper, true, []); }
        return cursor >= end.Next ? new(upper, false, records) : new(upper, true, []);
    }

    private static bool TryParse(string value, out Position position)
    {
        var parts = value.Split(':');
        if (parts.Length == 2 && ulong.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) &&
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var next) && next >= 0)
        { position = new(id, next); return true; }
        position = new(0, 0);
        return false;
    }

    private static SafeFileHandle? Open(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var mount = new StringBuilder(4096);
        var volume = new StringBuilder(1024);
        if (!GetVolumePathName(root, mount, (uint)mount.Capacity) ||
            !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, (uint)volume.Capacity))
            return null;
        var handle = CreateFile(volume.ToString().TrimEnd('\\'), 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    private static Journal? Query(SafeFileHandle handle)
    {
        var buffer = new byte[128];
        if (!DeviceIoControl(handle, QueryJournal, null, 0, buffer, (uint)buffer.Length, out var size, IntPtr.Zero) || size < 32)
            return null;
        return new(BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            Math.Max(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24))),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16)));
    }
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathName(string path, StringBuilder mount, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
