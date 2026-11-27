using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using StorageAgent.Contracts;

namespace StorageAgent.Client;

public sealed class StorageAgentException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>Transport-only client for local Storage Agent named-pipe calls.</summary>
public sealed class StorageAgentClient(TimeSpan? defaultTimeout = null)
{
    private readonly TimeSpan _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(10);

    public Task<StorageAgentStatus> GetStatusAsync(CancellationToken token = default) =>
        SendAsync<StorageAgentStatus>(StorageOperation.Status, timeout: _defaultTimeout, token: token);

    public Task<StorageSnapshot> GetInventoryAsync(CancellationToken token = default) =>
        SendAsync<StorageSnapshot>(StorageOperation.Inventory, timeout: _defaultTimeout, token: token);

    public Task<SystemReport> GetSystemReportAsync(CancellationToken token = default) =>
        SendAsync<SystemReport>(StorageOperation.SystemReport, token: token);

    public Task<StorageDisk> GetDiagnosticsAsync(string deviceId, CancellationToken token = default) =>
        SendAsync<StorageDisk>(StorageOperation.Diagnostics, deviceId: deviceId, timeout: _defaultTimeout, token: token);

    public Task<ResolvedStoragePath> ResolvePathAsync(string path, CancellationToken token = default) =>
        SendAsync<ResolvedStoragePath>(StorageOperation.ResolvePath, path: path, timeout: _defaultTimeout, token: token);

    public Task<StorageSnapshot> RefreshAsync(CancellationToken token = default) =>
        SendAsync<StorageSnapshot>(StorageOperation.Refresh, force: true, timeout: TimeSpan.FromSeconds(60), token: token);

    /// <summary>Waits for one change, or returns null after a bounded interval.</summary>
    public Task<StorageChange?> WaitForChangeAsync(int waitSeconds = 15, CancellationToken token = default)
    {
        var seconds = Math.Clamp(waitSeconds, 1, 60);
        return SendNullableAsync<StorageChange>(StorageOperation.Subscribe,
            waitSeconds: seconds, timeout: TimeSpan.FromSeconds(seconds + 5), token: token);
    }

    public async IAsyncEnumerable<StorageChange> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var change = await WaitForChangeAsync(30, cancellationToken);
            if (change is not null) yield return change;
        }
    }

    public Task<RemoteConnectionInfo> ConnectAsync(string serverUrl, string enrollmentCode,
        CancellationToken token = default) =>
        SendAsync<RemoteConnectionInfo>(StorageOperation.Connect, serverUrl: serverUrl,
            enrollmentCode: enrollmentCode, timeout: TimeSpan.FromSeconds(45), token: token);

    public Task<RemoteConnectionInfo> ConnectSystemAsync(string serverUrl, string enrollmentCode,
        IReadOnlyList<SystemCategory> categories, CancellationToken token = default, bool includeStorage = false) =>
        SendAsync<RemoteConnectionInfo>(StorageOperation.Connect, serverUrl: serverUrl, enrollmentCode: enrollmentCode,
            timeout: TimeSpan.FromSeconds(45), token: token, systemCategories: categories, allowStorage: includeStorage);

    public Task<IReadOnlyList<RemoteConnectionInfo>> ListConnectionsAsync(CancellationToken token = default) =>
        SendAsync<IReadOnlyList<RemoteConnectionInfo>>(StorageOperation.ConnectionsList, token: token);

    public Task<StorageControlResult> DisconnectAsync(string connectionId, CancellationToken token = default) =>
        SendAsync<StorageControlResult>(StorageOperation.Disconnect, connectionId: connectionId,
            timeout: TimeSpan.FromSeconds(45), token: token);

    public Task<RemoteSyncResult> SynchronizeAsync(CancellationToken token = default) =>
        SendAsync<RemoteSyncResult>(StorageOperation.Sync, timeout: TimeSpan.FromMinutes(2), token: token);

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines = 50, CancellationToken token = default) =>
        SendAsync<IReadOnlyList<string>>(StorageOperation.Logs, logLines: lines, token: token);

    public Task<bool> ConfigureFileWatchesAsync(string consumer, IReadOnlyList<string> roots, CancellationToken token = default) =>
        SendAsync<bool>(StorageOperation.FileWatchConfigure, token: token, fileWatch: new(consumer, roots));
    public Task<IReadOnlyList<FileWatchStatus>> GetFileWatchStatusAsync(string consumer, CancellationToken token = default) =>
        SendAsync<IReadOnlyList<FileWatchStatus>>(StorageOperation.FileWatchStatus, token: token, fileWatch: new(consumer));
    public Task<FileInventoryPage> GetFileInventoryPageAsync(string consumer, string root, long revision, int offset, CancellationToken token = default) =>
        SendAsync<FileInventoryPage>(StorageOperation.FileInventoryPage, token: token, fileWatch: new(consumer, Root: root, Revision: revision, Offset: offset));
    public Task<FileChangePage> GetFileChangesAsync(string consumer, string root, long cursor, CancellationToken token = default) =>
        SendAsync<FileChangePage>(StorageOperation.FileChanges, token: token, fileWatch: new(consumer, Root: root, Cursor: cursor));
    public Task<bool> RefreshFileWatchAsync(string consumer, string root, CancellationToken token = default) =>
        SendAsync<bool>(StorageOperation.FileWatchRefresh, token: token, fileWatch: new(consumer, Root: root));

    private async Task<T> SendAsync<T>(StorageOperation operation, string? deviceId = null, string? path = null,
        bool force = false, int? waitSeconds = null, TimeSpan? timeout = null, CancellationToken token = default,
        string? serverUrl = null, string? enrollmentCode = null, string? connectionId = null, int? logLines = null, IReadOnlyList<SystemCategory>? systemCategories = null, bool? allowStorage = null, FileWatchRequest? fileWatch = null)
    {
        var result = await SendNullableAsync<T>(operation, deviceId, path, force, waitSeconds, timeout, token,
            serverUrl, enrollmentCode, connectionId, logLines, systemCategories, allowStorage, fileWatch);
        return result ?? throw new StorageAgentException("response.empty_payload", "The Storage Agent returned an empty response.");
    }

    private async Task<T?> SendNullableAsync<T>(StorageOperation operation, string? deviceId = null, string? path = null,
        bool force = false, int? waitSeconds = null, TimeSpan? timeout = null, CancellationToken token = default,
        string? serverUrl = null, string? enrollmentCode = null, string? connectionId = null, int? logLines = null, IReadOnlyList<SystemCategory>? systemCategories = null, bool? allowStorage = null, FileWatchRequest? fileWatch = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Storage Agent named pipes are available only on Windows.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(timeout ?? _defaultTimeout);
        await using var pipe = new NamedPipeClientStream(".", StorageProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StorageAgentException("service.unavailable", "Storage Agent service is not available within the connection timeout.");
        }

        var request = new StorageRequest(StorageProtocol.Major, StorageProtocol.Minor, Guid.NewGuid().ToString("N"),
            operation, deviceId, path, force, waitSeconds, serverUrl, enrollmentCode, connectionId, logLines, systemCategories, allowStorage, fileWatch);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, StorageContractJson.Options);
        if (bytes.Length > StorageProtocol.MaxFrameBytes) throw new InvalidDataException("Request exceeds the local IPC frame limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        try
        {
            await pipe.WriteAsync(prefix, budget.Token);
            await pipe.WriteAsync(bytes, budget.Token);
            await pipe.FlushAsync(budget.Token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }

        var lengthBytes = new byte[4];
        await pipe.ReadExactlyAsync(lengthBytes, budget.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > StorageProtocol.MaxFrameBytes) throw new InvalidDataException("Service response frame size is invalid.");
        var responseBytes = new byte[length];
        await pipe.ReadExactlyAsync(responseBytes, budget.Token);
        var response = JsonSerializer.Deserialize<StorageResponse>(responseBytes, StorageContractJson.Options)
            ?? throw new InvalidDataException("Service response JSON is invalid.");
        if (response.ContractMajor != StorageProtocol.Major)
            throw new StorageAgentException("protocol.incompatible_major", "Storage Agent returned an incompatible contract version.");
        if (!response.Ok)
            throw new StorageAgentException(response.ErrorCode ?? "service.error", response.Error ?? "Storage Agent request failed.");
        return StorageContractJson.ReadPayload<T>(response);
    }
}
