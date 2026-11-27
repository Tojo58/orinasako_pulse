using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageAgent.Service;

/// <summary>Read-only NTFS journal high-water mark. Used as a reconciliation hint, not a replay of USN records.</summary>
internal static class UsnCheckpoint
{
    public static string? Read(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var drive = Path.GetPathRoot(root);
        if (drive is null || drive.Length != 3 || drive[1] != ':') return null;
        using var handle = CreateFile(@"\\.\" + drive[..2], 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new byte[128];
        if (!DeviceIoControl(handle, 0x000900F4, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var size, IntPtr.Zero) || size < 24)
            return null;
        return $"{BinaryPrimitives.ReadUInt64LittleEndian(buffer):X16}:{BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16))}";
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
