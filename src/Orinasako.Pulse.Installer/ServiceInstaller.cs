using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Orinasako.Pulse.Installer;

internal static class ServiceInstaller
{
    private const string Name = "OrinasakoPulse";
    private const string LegacyName = "StorageAgent";
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceErrorNormal = 1;
    private const uint ServiceStop = 0x20;
    private const uint ServiceStart = 0x10;
    private const uint ServiceQueryStatus = 4;
    private const uint ServiceControlStop = 1;
    private const uint ServiceStopped = 1;
    private const uint ServiceStopPending = 3;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigFailureActions = 2;
    private const uint ServiceConfigFailureActionsFlag = 4;
    private const uint ServiceConfigServiceSidInfo = 5;
    private const uint ServiceSidTypeUnrestricted = 1;
    private const uint ScActionRestart = 1;
    private const uint SeFileObject = 1;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;

    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Orinasako Pulse");
    private static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StorageAgent");

    public static Version? InstalledVersion()
    {
        var executable = Path.Combine(InstallDirectory, "Orinasako.Pulse.Service.exe");
        if (!File.Exists(executable)) return null;
        return Version.TryParse(FileVersionInfo.GetVersionInfo(executable).FileVersion, out var version) ? version : null;
    }

    public static string QueryStatus()
    {
        using var manager = OpenScManager();
        var service = OpenService(manager, Name, ServiceQueryStatus);
        if (service.IsInvalid) return Marshal.GetLastWin32Error() == 1060 ? "not-installed" : "unknown";
        using (service)
        {
            var buffer = new byte[64];
            if (!QueryServiceStatusEx(service, 0, buffer, (uint)buffer.Length, out _)) return "unknown";
            return BitConverter.ToUInt32(buffer, 4) switch
            {
                1 => "stopped", 2 => "start-pending", 3 => "stop-pending", 4 => "running",
                5 => "continue-pending", 6 => "pause-pending", 7 => "paused", _ => "unknown"
            };
        }
    }

    public static void StartIfInstalled()
    {
        using var manager = OpenScManager();
        var service = OpenService(manager, Name, ServiceStart | ServiceQueryStatus);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (service)
        {
            var state = QueryServiceState(service);
            if (state == 4) return;
            if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void WaitUntilRunning()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var state = QueryStatus();
            if (state == "running") return;
            if (state is "stopped" or "not-installed")
                throw new IOException("Orinasako Pulse failed to start.");
            Thread.Sleep(250);
        }
        throw new TimeoutException("Orinasako Pulse did not reach the running state.");
    }

    public static void InstallOrUpdate(string source)
    {
        var sourceDirectory = Path.GetFullPath(source);
        var sourceService = Path.Combine(sourceDirectory, "Orinasako.Pulse.Service.exe");
        if (!File.Exists(sourceService)) throw new FileNotFoundException("Service executable is missing from the installer payload.", sourceService);
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(DataDirectory);
        SetProtectedDirectoryAcl(InstallDirectory, allowExecute: true);
        SetProtectedDirectoryAcl(DataDirectory);
        // Do not run both service identities against the same data directory and IPC pipe.
        StopIfInstalled(LegacyName);
        try
        {
            StopIfInstalled();
            CopyFiles(sourceDirectory, InstallDirectory);
            ConfigureService();
            StartIfInstalled();
            WaitUntilRunning();
        }
        catch
        {
            StopIfInstalled();
            using var manager = OpenScManager();
            using var legacy = OpenService(manager, LegacyName, ServiceStart | ServiceQueryStatus);
            if (!legacy.IsInvalid && QueryServiceState(legacy) == ServiceStopped)
                _ = StartService(legacy, 0, IntPtr.Zero);
            throw;
        }
        using (var manager = OpenScManager())
        using (var legacy = OpenService(manager, LegacyName, ServiceAllAccess))
        {
            if (!legacy.IsInvalid && !DeleteService(legacy) && Marshal.GetLastWin32Error() != 1060)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void Uninstall(bool removeData)
    {
        StopIfInstalled();
        using (var manager = OpenScManager())
        {
            var service = OpenService(manager, Name, ServiceAllAccess);
            if (!service.IsInvalid)
            {
                using (service)
                {
                    if (!DeleteService(service) && Marshal.GetLastWin32Error() != 1060)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }
        if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, recursive: true);
        if (removeData && Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
    }

    private static void CopyFiles(string source, string destination)
    {
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourcePrefix = source + Path.DirectorySeparatorChar;
        var destinationPrefix = destination + Path.DirectorySeparatorChar;
        if (source.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer payload and destination must not contain one another.");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ConfigureService()
    {
        using var manager = OpenScManager();
        var binary = '"' + Path.Combine(InstallDirectory, "Orinasako.Pulse.Service.exe") + '"';
        var service = CreateService(manager, Name, "Orinasako Pulse", ServiceAllAccess,
            ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, binary, null, IntPtr.Zero,
            null, null, null);
        if (service.IsInvalid && Marshal.GetLastWin32Error() == 1073)
        {
            service.Dispose();
            service = OpenService(manager, Name, ServiceAllAccess);
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!ChangeServiceConfig(service, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                binary, null, IntPtr.Zero, null, null, null, "Orinasako Pulse"))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (service)
        {
            SetServiceDescription(service, "Generic Windows system monitoring and file inventory agent; exposes only a local named pipe.");
            var sidType = Marshal.AllocHGlobal(sizeof(uint));
            try { Marshal.WriteInt32(sidType, (int)ServiceSidTypeUnrestricted); SetServiceInfo(service, ServiceConfigServiceSidInfo, sidType); }
            finally { Marshal.FreeHGlobal(sidType); }
            ConfigureRecovery(service);
        }
    }

    private static void ConfigureRecovery(SafeServiceHandle service)
    {
        var actionSize = Marshal.SizeOf<ScAction>();
        var actionArray = Marshal.AllocHGlobal(actionSize * 3);
        var description = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceFailureActions>());
        var flag = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.StructureToPtr(new ScAction(ScActionRestart, 10_000), actionArray, false);
            Marshal.StructureToPtr(new ScAction(ScActionRestart, 30_000), actionArray + actionSize, false);
            Marshal.StructureToPtr(new ScAction(ScActionRestart, 60_000), actionArray + actionSize * 2, false);
            Marshal.StructureToPtr(new ServiceFailureActions(86_400, IntPtr.Zero, IntPtr.Zero, 3, actionArray), description, false);
            SetServiceInfo(service, ServiceConfigFailureActions, description);
            Marshal.WriteInt32(flag, 1);
            SetServiceInfo(service, ServiceConfigFailureActionsFlag, flag);
        }
        finally
        {
            Marshal.FreeHGlobal(actionArray);
            Marshal.FreeHGlobal(description);
            Marshal.FreeHGlobal(flag);
        }
    }

    private static void SetServiceInfo(SafeServiceHandle service, uint level, IntPtr buffer)
    {
        if (!ChangeServiceConfig2(service, level, buffer)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void SetServiceDescription(SafeServiceHandle service, string value)
    {
        var text = Marshal.StringToHGlobalUni(value);
        var description = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceDescription>());
        try
        {
            Marshal.StructureToPtr(new ServiceDescription(text), description, false);
            SetServiceInfo(service, ServiceConfigDescription, description);
        }
        finally { Marshal.FreeHGlobal(text); Marshal.FreeHGlobal(description); }
    }

    private static void StopIfInstalled(string name = Name)
    {
        using var manager = OpenScManager();
        var service = OpenService(manager, name, ServiceStop | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            if (Marshal.GetLastWin32Error() == 1060) return;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using (service)
        {
            var status = QueryServiceState(service);
            if (status == ServiceStopped) return;
            if (status != ServiceStopPending && !ControlService(service, ServiceControlStop, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1062) throw new Win32Exception(error);
            }
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (QueryServiceState(service) == ServiceStopped) return;
                Thread.Sleep(300);
            }
            throw new TimeoutException("Orinasako Pulse did not stop within 60 seconds.");
        }
    }

    private static uint QueryServiceState(SafeServiceHandle service)
    {
        var buffer = new byte[64];
        if (!QueryServiceStatusEx(service, 0, buffer, (uint)buffer.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return BitConverter.ToUInt32(buffer, 4);
    }

    private static SafeScmHandle OpenScManager()
    {
        var handle = OpenSCManager(null, null, ScManagerAllAccess);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    private static void SetProtectedDirectoryAcl(string directory, bool allowExecute = false)
    {
        var sddl = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)" + (allowExecute ? "(A;OICI;GRGX;;;AU)" : "");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetSecurityDescriptorDacl(descriptor, out var present, out var dacl, out _) || !present)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = SetNamedSecurityInfo(directory, SeFileObject,
                DaclSecurityInformation | ProtectedDaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            if (result != 0) throw new Win32Exception(checked((int)result));
        }
        finally { LocalFree(descriptor); }
    }

    private sealed class SafeScmHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeScmHandle() : base(true) { }
        public SafeScmHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    private sealed class SafeServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle() : base(true) { }
        public SafeServiceHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct ScAction(uint Type, uint Delay);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ServiceFailureActions(uint ResetPeriod, IntPtr RebootMessage, IntPtr Command, uint ActionsCount, IntPtr Actions);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ServiceDescription(IntPtr Description);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeScmHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle CreateService(SafeScmHandle manager, string name, string displayName, uint desiredAccess,
        uint serviceType, uint startType, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeScmHandle manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(SafeServiceHandle service, uint type, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string displayName);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2(SafeServiceHandle service, uint infoLevel, IntPtr info);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(SafeServiceHandle service, uint argumentCount, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, byte[] buffer, uint bufferSize, out uint bytesNeeded);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(SafeServiceHandle service);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);
    [DllImport("advapi32.dll", EntryPoint = "SetNamedSecurityInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetNamedSecurityInfo(string objectName, uint objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)] private readonly record struct ServiceStatus(uint ServiceType, uint CurrentState, uint ControlsAccepted,
        uint Win32ExitCode, uint ServiceSpecificExitCode, uint CheckPoint, uint WaitHint);
}
