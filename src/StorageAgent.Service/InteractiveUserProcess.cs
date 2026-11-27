using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StorageAgent.Service;

/// <summary>Launches a bounded read-only SMB worker under the active Windows user.</summary>
internal static class InteractiveUserProcess
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const uint ActiveSessionState = 0;
    private const uint CreateNoWindow = 0x08000000;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo
    {
        public uint SessionId;
        public IntPtr StationName;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr server, uint reserved, uint version, out IntPtr sessions, out uint count);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurityW(string path, uint information, IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token,
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <returns>null when there is no active interactive user or this is not Windows.</returns>
    public static Process? TryStart(string executable, IReadOnlyList<string> arguments, string outputPath, out string? principalKey)
    {
        principalKey = null;
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var sessionId in ActiveSessionIds())
        {
            if (!WTSQueryUserToken(sessionId, out var token)) continue;
            try
            {
                var sid = TokenSid(token);
                principalKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("filesystem-principal:" + sid)));
                GrantOutputFileToUser(outputPath, sid);
                return Start(token, executable, arguments);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        return null;
    }

    private static IReadOnlyList<uint> ActiveSessionIds()
    {
        var active = new List<uint>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count))
        {
            var consoleFallback = WTSGetActiveConsoleSessionId();
            return consoleFallback == InvalidSessionId ? active : [consoleFallback];
        }
        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var current = Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(sessions, index * size));
                if (current.State == ActiveSessionState && !active.Contains(current.SessionId))
                    active.Add(current.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
        var consoleSession = WTSGetActiveConsoleSessionId();
        if (consoleSession != InvalidSessionId && active.Remove(consoleSession))
            active.Insert(0, consoleSession);
        return active;
    }

    private static string TokenSid(IntPtr token)
    {
        _ = GetTokenInformation(token, 1, IntPtr.Zero, 0, out var size);
        if (size <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 1, buffer, size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var user = Marshal.PtrToStructure<TokenUser>(buffer);
            if (!ConvertSidToStringSidW(user.Sid, out var text))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("SID utilisateur Windows absent."); }
            finally { LocalFree(text); }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void GrantOutputFileToUser(string path, string sid)
    {
        // The output is a short-lived scan snapshot. Only SYSTEM and this user's
        // token may read or write it; the ACL is protected from parent inheritance.
        var sddl = $"D:P(A;;FA;;;SY)(A;;GRGW;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var securityInformation = DaclSecurityInformation | ProtectedDaclSecurityInformation;
            if (!SetFileSecurityW(path, securityInformation, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static Process Start(IntPtr token, string executable, IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(Quote(executable));
        foreach (var argument in arguments)
            commandLine.Append(' ').Append(Quote(argument));

        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessAsUserW(
            token,
            executable,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            CreateNoWindow,
            IntPtr.Zero,
            Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            ref startup,
            out var information))
            throw new IOException(
                "Impossible de lancer le scan SMB avec l’identité Windows connectée.",
                new Win32Exception(Marshal.GetLastWin32Error()));

        try
        {
            try { return Process.GetProcessById(checked((int)information.ProcessId)); }
            catch (ArgumentException error)
            {
                throw new IOException("Le processus de scan SMB s’est arrêté avant son suivi.", error);
            }
        }
        finally
        {
            CloseHandle(information.Thread);
            CloseHandle(information.Process);
        }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }
}
