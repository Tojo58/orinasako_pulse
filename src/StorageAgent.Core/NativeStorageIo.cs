using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageAgent.Core;

internal static class NativeStorageIo
{
    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const uint IoctlDiskGetLengthInfo = 0x0007405C;
    internal const uint IoctlVolumeGetDiskExtents = 0x00560000;
    internal const uint IoctlAtaPassThrough = 0x0004D02C;
    internal const uint IoctlScsiPassThrough = 0x0004D004;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        [In] byte[]? input, uint inputLength, [Out] byte[]? output, uint outputLength,
        out uint bytesReturned, IntPtr overlapped);

    internal static SafeFileHandle Open(string path, bool allowReadOnlyCommands = false)
    {
        var access = GenericRead | (allowReadOnlyCommands ? GenericWrite : 0);
        var handle = CreateFile(path, access, 0x00000001 | 0x00000002 | 0x00000004,
            IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    internal static byte[]? QueryProperty(SafeFileHandle handle, uint propertyId, int outputLength = 4096)
    {
        var input = new byte[8];
        BitConverter.GetBytes(propertyId).CopyTo(input, 0);
        var output = new byte[outputLength];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, input, (uint)input.Length,
            output, (uint)output.Length, out var returned, IntPtr.Zero) || returned < 8)
            return null;
        Array.Resize(ref output, checked((int)returned));
        return output;
    }

    internal static long? QueryCapacity(SafeFileHandle handle)
    {
        var output = new byte[8];
        return DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, output, 8, out var returned, IntPtr.Zero) && returned >= 8
            ? BitConverter.ToInt64(output)
            : null;
    }

    internal static byte[]? QueryNvme(SafeFileHandle handle, uint propertyId, uint dataType, uint requestValue, int dataLength)
    {
        const int queryHeader = 8;
        const int protocolDataSize = 40;
        var buffer = new byte[queryHeader + protocolDataSize + dataLength];
        BitConverter.GetBytes(propertyId).CopyTo(buffer, 0);
        BitConverter.GetBytes(0u).CopyTo(buffer, 4); // PropertyStandardQuery
        BitConverter.GetBytes(3u).CopyTo(buffer, 8); // ProtocolTypeNvme
        BitConverter.GetBytes(dataType).CopyTo(buffer, 12);
        BitConverter.GetBytes(requestValue).CopyTo(buffer, 16);
        BitConverter.GetBytes(0u).CopyTo(buffer, 20);
        BitConverter.GetBytes((uint)protocolDataSize).CopyTo(buffer, 24);
        BitConverter.GetBytes((uint)dataLength).CopyTo(buffer, 28);
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, (uint)buffer.Length,
            buffer, (uint)buffer.Length, out var returned, IntPtr.Zero) || returned < queryHeader + protocolDataSize)
            return null;
        var version = BitConverter.ToUInt32(buffer, 0);
        var size = BitConverter.ToUInt32(buffer, 4);
        var dataOffset = BitConverter.ToUInt32(buffer, 24);
        var actualLength = BitConverter.ToUInt32(buffer, 28);
        var start = checked(queryHeader + (int)dataOffset);
        if (version < queryHeader + protocolDataSize || size < queryHeader + protocolDataSize ||
            dataOffset < protocolDataSize || actualLength < dataLength || start < 0 || start + dataLength > returned)
            return null;
        return buffer.AsSpan(start, dataLength).ToArray();
    }

    internal static byte[]? QueryAta(SafeFileHandle handle, byte command, byte features = 0, byte sectorCount = 1, bool hasData = true)
    {
        var headerLength = IntPtr.Size == 8 ? 48 : 40;
        const int dataLength = 512;
        var buffer = new byte[headerLength + (hasData ? dataLength : 0)];
        BitConverter.GetBytes((ushort)headerLength).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(0x02 | 0x01)).CopyTo(buffer, 2); // DATA_IN | DRDY_REQUIRED; commands below are reads only.
        BitConverter.GetBytes(hasData ? (uint)dataLength : 0u).CopyTo(buffer, 8);
        BitConverter.GetBytes(5u).CopyTo(buffer, 12);
        if (IntPtr.Size == 8) BitConverter.GetBytes((ulong)headerLength).CopyTo(buffer, 24);
        else BitConverter.GetBytes((uint)headerLength).CopyTo(buffer, 20);
        var taskFile = IntPtr.Size == 8 ? 40 : 32;
        buffer[taskFile] = features;
        buffer[taskFile + 1] = sectorCount;
        buffer[taskFile + 3] = 0x4F;
        buffer[taskFile + 4] = 0xC2;
        buffer[taskFile + 6] = command;
        var inputLength = hasData ? (uint)headerLength : (uint)headerLength;
        if (!DeviceIoControl(handle, IoctlAtaPassThrough, buffer, inputLength, buffer,
            (uint)buffer.Length, out var returned, IntPtr.Zero) || returned < headerLength)
            return null;
        return hasData && returned >= headerLength + dataLength ? buffer.AsSpan(headerLength, dataLength).ToArray() : buffer;
    }

    internal static IReadOnlyList<int> QueryVolumeDiskNumbers(SafeFileHandle volume)
    {
        var output = new byte[4096];
        if (!DeviceIoControl(volume, IoctlVolumeGetDiskExtents, null, 0, output, (uint)output.Length,
            out var returned, IntPtr.Zero) || returned < 4) return [];
        var count = Math.Min(BitConverter.ToUInt32(output, 0), 128u);
        var values = new List<int>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            var offset = 8 + checked((int)i) * 24;
            if (offset + 4 > returned) break;
            var number = BitConverter.ToInt32(output, offset);
            if (number >= 0) values.Add(number);
        }
        return values;
    }
}
