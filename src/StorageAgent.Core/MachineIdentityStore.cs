using System.Security.Cryptography;

namespace StorageAgent.Core;

public interface IMachineKeyProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

/// <summary>HMAC-pseudonymizes physical identifiers; the source identifier never leaves the process.</summary>
public sealed class MachineIdentityStore(string? keyFile = null, IMachineKeyProtector? protector = null)
{
    private readonly string _keyFile = keyFile ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StorageAgent", "identity.key");
    private readonly object _gate = new();
    private byte[]? _key;

    public string CreateDiskId(string privateHardwareIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateHardwareIdentifier);
        var hash = HMACSHA256.HashData(GetKey(), System.Text.Encoding.UTF8.GetBytes(privateHardwareIdentifier));
        return Convert.ToHexString(hash.AsSpan(0, 20));
    }

    public string CreateVolumeId(string volumeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeIdentifier);
        var hash = HMACSHA256.HashData(GetKey(), System.Text.Encoding.UTF8.GetBytes("volume|" + volumeIdentifier));
        return Convert.ToHexString(hash.AsSpan(0, 20));
    }

    public string CreateAgentId(string serverUrl)
    {
        var scope = CanonicalServerScope(serverUrl);
        var input = System.Text.Encoding.UTF8.GetBytes($"agent|{scope}");
        var hash = HMACSHA256.HashData(GetKey(), input);
        CryptographicOperations.ZeroMemory(input);
        return Convert.ToHexString(hash.AsSpan(0, 20));
    }

    public string CreateRemoteDeviceId(string serverUrl, string kind, string localId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        var scope = CanonicalServerScope(serverUrl);
        var input = System.Text.Encoding.UTF8.GetBytes($"{kind}|{scope}|{localId}");
        var hash = HMACSHA256.HashData(GetKey(), input);
        CryptographicOperations.ZeroMemory(input);
        return Convert.ToHexString(hash.AsSpan(0, 20));
    }

    private static string CanonicalServerScope(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
            throw new ArgumentException("A valid HTTPS server address is required.", nameof(serverUrl));
        return server.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private byte[] GetKey()
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            if (OperatingSystem.IsWindows() && protector is null)
                throw new InvalidOperationException("Windows machine identity keys must be protected by DPAPI.");
            if (File.Exists(_keyFile))
            {
                var existing = File.ReadAllBytes(_keyFile);
                var unprotected = protector?.Unprotect(existing) ?? existing.ToArray();
                CryptographicOperations.ZeroMemory(existing);
                if (unprotected.Length != 32) throw new InvalidDataException("Machine identity key is not 32 bytes.");
                _key = unprotected;
                return unprotected;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
            var candidate = RandomNumberGenerator.GetBytes(32);
            var persisted = protector?.Protect(candidate) ?? candidate;
            try
            {
                using var stream = new FileStream(_keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(persisted);
                stream.Flush(flushToDisk: true);
                _key = candidate;
                return candidate;
            }
            catch (IOException) when (File.Exists(_keyFile))
            {
                CryptographicOperations.ZeroMemory(candidate);
                var existing = File.ReadAllBytes(_keyFile);
                var unprotected = protector?.Unprotect(existing) ?? existing.ToArray();
                CryptographicOperations.ZeroMemory(existing);
                if (unprotected.Length != 32) throw new InvalidDataException("Machine identity key is not 32 bytes.");
                _key = unprotected;
                return unprotected;
            }
            finally { if (!ReferenceEquals(persisted, candidate)) CryptographicOperations.ZeroMemory(persisted); }
        }
    }
}
