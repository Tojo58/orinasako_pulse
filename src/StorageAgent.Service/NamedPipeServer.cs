using StorageAgent.Transport;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using StorageAgent.Contracts;
using StorageAgent.Core;

namespace StorageAgent.Service;

internal sealed class NamedPipeServer(
    StorageRuntimeState state,
    SystemRuntimeState systemState,
    StorageChangeHub changes,
    IStorageCollector collector,
    RemoteConnectionManager remoteConnections,
    StorageFileLoggerProvider fileLogger,
    ILogger<NamedPipeServer> logger) : BackgroundService
{
    private const int MaxClients = 16;
    private const int BufferSize = 64 * 1024;
    private readonly SemaphoreSlim _clientLimit = new(MaxClients, MaxClients);
    private static readonly string ServiceVersion = typeof(NamedPipeServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("Named pipe service is available only on Windows.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            await _clientLimit.WaitAsync(stoppingToken);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                var clientPipe = pipe;
                pipe = null;
                _ = HandleAndReleaseAsync(clientPipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                _clientLimit.Release();
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                pipe?.Dispose();
                _clientLimit.Release();
                logger.LogWarning("Named pipe connection failed ({Type}, 0x{HResult:X8}).", error.GetType().Name, error.HResult);
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task HandleAndReleaseAsync(NamedPipeServerStream pipe, CancellationToken serviceToken)
    {
        try { await using (pipe) await HandleAsync(pipe, serviceToken); }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or OperationCanceledException)
        {
            if (!serviceToken.IsCancellationRequested) logger.LogDebug("Named pipe client request ended ({Type}).", error.GetType().Name);
        }
        finally { _clientLimit.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreatePipe()
    {
        const uint pipeAccessDuplex = 0x00000003;
        const uint fileFlagOverlapped = 0x40000000;
        const uint pipeTypeByte = 0x00000000;
        const uint pipeReadModeByte = 0x00000000;
        const uint pipeWait = 0x00000000;
        const uint rejectRemoteClients = 0x00000008;
        const string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;AU)";

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new NativeSecurityAttributes
            {
                Length = Marshal.SizeOf<NativeSecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            var handle = CreateNamedPipe($"\\\\.\\pipe\\{StorageProtocol.PipeName}",
                pipeAccessDuplex | fileFlagOverlapped, pipeTypeByte | pipeReadModeByte | pipeWait | rejectRemoteClients,
                MaxClients, BufferSize, BufferSize, 0, ref security);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { LocalFree(descriptor); }
    }

    [SupportedOSPlatform("windows")]
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var requestBytes = await ReadFrameAsync(pipe, token);
        StorageRequest? request;
        try { request = JsonSerializer.Deserialize<StorageRequest>(requestBytes, StorageContractJson.Options); }
        catch (JsonException)
        {
            await WriteFrameAsync(pipe, StorageContractJson.Failure("invalid", "request.invalid_json", "Request JSON is invalid."), token);
            return;
        }
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 80)
        {
            await WriteFrameAsync(pipe, StorageContractJson.Failure("invalid", "request.invalid", "Request envelope is invalid."), token);
            return;
        }
        if (request.ContractMajor != StorageProtocol.Major)
        {
            await WriteFrameAsync(pipe, StorageContractJson.Failure(request.RequestId, "protocol.incompatible_major",
                $"Client protocol major {request.ContractMajor} is not supported; service protocol is {StorageProtocol.Major}."), token);
            return;
        }

        if ((request.Operation is StorageOperation.Connect or StorageOperation.Disconnect or StorageOperation.SystemReport) && !IsAdministrator(pipe))
        {
            await WriteFrameAsync(pipe, StorageContractJson.Failure(request.RequestId, "access.administrator_required",
                "Run this Storage Agent command from an elevated Administrator session."), token);
            return;
        }

        StorageResponse response;
        try
        {
            if (request.Operation == StorageOperation.Inventory && state.Snapshot is null)
            {
                await WriteFrameAsync(pipe, StorageContractJson.Failure(request.RequestId, "inventory.not_ready",
                    "The first storage inventory has not completed yet."), token);
                return;
            }
            response = request.Operation switch
            {
                StorageOperation.Status => StorageContractJson.Success(request.RequestId, state.Status(ServiceVersion)),
                StorageOperation.Inventory => StorageContractJson.Success(request.RequestId, state.Snapshot),
                StorageOperation.SystemReport => StorageContractJson.Success(request.RequestId, new SystemReport(state.Snapshot, systemState.Snapshot())),
                StorageOperation.Diagnostics => Diagnostics(request),
                StorageOperation.ResolvePath => ResolvePath(request),
                StorageOperation.Refresh => StorageContractJson.Success(request.RequestId,
                    await state.RefreshAsync(collector, request.Force, token)),
                StorageOperation.Subscribe => await SubscribeAsync(request, token),
                StorageOperation.Connect => StorageContractJson.Success(request.RequestId,
                    await ConnectAsync(request, token)),
                StorageOperation.ConnectionsList => StorageContractJson.Success(request.RequestId, remoteConnections.List()),
                StorageOperation.Disconnect => await DisconnectAsync(request, token),
                StorageOperation.Sync => StorageContractJson.Success(request.RequestId,
                    await remoteConnections.SynchronizeAsync(token)),
                StorageOperation.Logs => StorageContractJson.Success(request.RequestId,
                    fileLogger.ReadLast(request.LogLines ?? 50)),
                _ => StorageContractJson.Failure(request.RequestId, "request.unsupported_operation", "Operation is not supported.")
            };
        }
        catch (RemoteProtocolException error)
        {
            response = StorageContractJson.Failure(request.RequestId, error.Code, error.Message);
        }
        catch (InvalidDataException error)
        {
            response = StorageContractJson.Failure(request.RequestId, "request.invalid_argument", error.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Storage IPC operation {Operation} failed ({Type}, 0x{HResult:X8}).", request.Operation, error.GetType().Name, error.HResult);
            response = StorageContractJson.Failure(request.RequestId, "service.operation_failed", "The requested storage operation could not be completed.");
        }
        await WriteFrameAsync(pipe, response, token);
    }

    private async Task<RemoteConnectionInfo> ConnectAsync(StorageRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ServerUrl) || request.ServerUrl.Length > 2048 ||
            string.IsNullOrWhiteSpace(request.EnrollmentCode) || request.EnrollmentCode.Length > 256)
            throw new InvalidDataException("A server address and one-time enrollment code are required.");
        if (request.SystemCategories is { } categories && (categories.Count > 11 || categories.Any(category => !Enum.IsDefined(category))))
            throw new InvalidDataException("Unknown or excessive system categories.");
        if (request.AllowStorage == false && request.SystemCategories is not { Count: > 0 })
            throw new InvalidDataException("At least one collector must be authorized.");
        return await remoteConnections.ConnectAsync(request.ServerUrl, request.EnrollmentCode, token, request.SystemCategories, request.AllowStorage);
    }

    private async Task<StorageResponse> DisconnectAsync(StorageRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionId) || request.ConnectionId.Length > 100)
            throw new InvalidDataException("A server connection id is required.");
        await remoteConnections.DisconnectAsync(request.ConnectionId, token);
        return StorageContractJson.Success(request.RequestId, new StorageControlResult(true,
            "The local connection was disabled. Remote revocation is retried automatically if the server is offline."));
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator(NamedPipeServerStream pipe)
    {
        var isAdministrator = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        });
        return isAdministrator;
    }

    private StorageResponse Diagnostics(StorageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId)) throw new InvalidDataException("A device id is required.");
        var disk = state.Snapshot?.Disks.FirstOrDefault(item => item.Id == request.DeviceId);
        return disk is null
            ? StorageContractJson.Failure(request.RequestId, "device.not_found", "The requested device is not in the latest inventory.")
            : StorageContractJson.Success(request.RequestId, disk);
    }

    private StorageResponse ResolvePath(StorageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 32768) throw new InvalidDataException("A local path is required.");
        if (request.Path.StartsWith("\\\\", StringComparison.Ordinal) || request.Path.StartsWith("//", StringComparison.Ordinal))
            return StorageContractJson.Success(request.RequestId, new ResolvedStoragePath(request.Path, true, [], [],
                "Network paths have no local physical-disk SMART mapping; NAS hardware is not queried by this agent."));
        string fullPath;
        try { fullPath = Path.GetFullPath(request.Path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The path format is invalid."); }
        var match = state.Snapshot?.Volumes.Where(volume => volume.Kind != StorageVolumeKind.Network &&
                volume.MountPoints.Any(mount => IsPathWithin(fullPath, mount)))
            .OrderByDescending(volume => volume.MountPoints.Max(static mount => mount.Length)).FirstOrDefault();
        return StorageContractJson.Success(request.RequestId, new ResolvedStoragePath(request.Path, false,
            match?.PhysicalDiskIds ?? [], match is null ? [] : [match.Id],
            match is null ? "No local volume mapping is available for this path." : null));
    }

    private async Task<StorageResponse> SubscribeAsync(StorageRequest request, CancellationToken token)
    {
        var seconds = Math.Clamp(request.WaitSeconds ?? 30, 1, 60);
        var change = await changes.WaitAsync(TimeSpan.FromSeconds(seconds), token);
        return StorageContractJson.Success(request.RequestId, change);
    }

    private static bool IsPathWithin(string path, string mount)
    {
        var normalizedMount = Path.GetFullPath(mount);
        var normalizedPath = Path.GetFullPath(path);
        if (normalizedPath.Equals(normalizedMount, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = normalizedMount.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedMount : normalizedMount + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var lengthBuffer = new byte[4];
        await stream.ReadExactlyAsync(lengthBuffer, token);
        var length = BitConverter.ToInt32(lengthBuffer);
        if (length is <= 0 or > StorageProtocol.MaxFrameBytes) throw new InvalidDataException("IPC frame size is outside the allowed range.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, StorageResponse response, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, StorageContractJson.Options);
        if (payload.Length > StorageProtocol.MaxFrameBytes) throw new InvalidDataException("IPC response exceeds the allowed frame size.");
        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, int maxInstances,
        int outBufferSize, int inBufferSize, uint defaultTimeout, ref NativeSecurityAttributes securityAttributes);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
