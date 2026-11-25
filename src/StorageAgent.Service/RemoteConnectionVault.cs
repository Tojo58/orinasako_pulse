using System.Security.Cryptography;
using System.Text.Json;
using StorageAgent.Contracts;

namespace StorageAgent.Service;

internal sealed record StoredRemoteConnection(
    RemoteConnectionInfo Info,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt,
    RemoteSnapshotEnvelope? PendingSnapshot,
    IReadOnlyList<RemoteChangeEnvelope> PendingEvents,
    IReadOnlyList<string> ProcessedCommandIds,
    IReadOnlyList<RemoteCommandResult>? PendingCommandResults = null,
    string? PendingRefreshRequestId = null);

internal sealed record RemoteConnectionVaultContent(IReadOnlyList<StoredRemoteConnection> Connections);

public interface ISecretProtector : StorageAgent.Core.IMachineKeyProtector { }

internal sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "StorageAgent.ConnectionVault.v1"u8.ToArray();

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Connection secrets require Windows DPAPI.");
        return ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.LocalMachine);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Connection secrets require Windows DPAPI.");
        return ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.LocalMachine);
    }
}

/// <summary>Stores tokens and each server's offline queue in one DPAPI-protected, atomic file.</summary>
internal sealed class RemoteConnectionVault(ISecretProtector protector, string? filePath = null)
{
    private const int MaximumVaultBytes = 32 * 1024 * 1024;
    private const int MaximumPendingEventsPerConnection = 1000;
    private static readonly JsonSerializerOptions Json = StorageContractJson.Options;
    private readonly string _filePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StorageAgent", "connections.vault");
    private readonly object _gate = new();

    public IReadOnlyList<RemoteConnectionInfo> List()
    {
        lock (_gate) return Load().Connections.Select(static item => item.Info).OrderBy(static item => item.ConnectedAt).ToArray();
    }

    public IReadOnlyList<StoredRemoteConnection> ReadAll()
    {
        lock (_gate) return Load().Connections.ToArray();
    }

    public StoredRemoteConnection? Find(string connectionId)
    {
        lock (_gate) return Load().Connections.FirstOrDefault(item =>
            string.Equals(item.Info.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase));
    }

    public void Add(StoredRemoteConnection connection)
    {
        lock (_gate)
        {
            var current = Load();
            if (current.Connections.Any(item => string.Equals(item.Info.ConnectionId, connection.Info.ConnectionId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A connection with this id already exists.");
            if (current.Connections.Any(item => string.Equals(item.Info.ServerUrl, connection.Info.ServerUrl, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This server already has a connection. Disconnect it before pairing again.");
            Save(current with { Connections = [.. current.Connections, connection] });
        }
    }

    public bool Update(string connectionId, Func<StoredRemoteConnection, StoredRemoteConnection> update)
    {
        lock (_gate)
        {
            var current = Load();
            var index = -1;
            for (var i = 0; i < current.Connections.Count; i++)
                if (string.Equals(current.Connections[i].Info.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            if (index < 0) return false;
            var next = current.Connections.ToArray();
            next[index] = update(next[index]);
            if (next[index].PendingEvents.Count > MaximumPendingEventsPerConnection)
                throw new InvalidOperationException("The pending event queue exceeds its configured limit.");
            if (next[index].ProcessedCommandIds.Count > MaximumPendingEventsPerConnection)
                next[index] = next[index] with { ProcessedCommandIds = next[index].ProcessedCommandIds.TakeLast(MaximumPendingEventsPerConnection).ToArray() };
            if ((next[index].PendingCommandResults?.Count ?? 0) > 8)
                throw new InvalidOperationException("The pending command result queue exceeds its configured limit.");
            Save(current with { Connections = next });
            return true;
        }
    }

    public bool Remove(string connectionId)
    {
        lock (_gate)
        {
            var current = Load();
            var next = current.Connections.Where(item => !string.Equals(item.Info.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (next.Length == current.Connections.Count) return false;
            Save(current with { Connections = next });
            return true;
        }
    }

    private RemoteConnectionVaultContent Load()
    {
        if (!File.Exists(_filePath)) return new RemoteConnectionVaultContent([]);
        var ciphertext = File.ReadAllBytes(_filePath);
        if (ciphertext.Length is <= 0 or > MaximumVaultBytes)
            throw new InvalidDataException("The encrypted connection vault has an invalid size.");
        var plaintext = protector.Unprotect(ciphertext);
        try
        {
            return JsonSerializer.Deserialize<RemoteConnectionVaultContent>(plaintext, Json)
                ?? throw new InvalidDataException("The connection vault content is invalid.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The connection vault content is invalid.", error);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void Save(RemoteConnectionVaultContent content)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(content, Json);
        if (plaintext.Length > MaximumVaultBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("The connection vault exceeds its configured size limit.");
        }
        byte[] ciphertext;
        try { ciphertext = protector.Protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }

        var tempPath = _filePath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                65536, FileOptions.WriteThrough))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
