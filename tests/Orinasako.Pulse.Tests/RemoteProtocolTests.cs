using Orinasako.Pulse.Transport;
using System.Net;
using System.Text;
using System.Text.Json;
using Orinasako.Pulse.Contracts;
using Orinasako.Pulse.Service;

namespace Orinasako.Pulse.Tests;

public sealed class RemoteProtocolTests
{
    [Fact]
    public void VaultEncryptsCredentialsAndPersistsOfflineDataPerConnection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storage-agent-vault-{Guid.NewGuid():N}.bin");
        try
        {
            var vault = new RemoteConnectionVault(new TestSecretProtector(), path);
            var snapshot = CreateSnapshot();
            var deliveryId = Guid.NewGuid().ToString();
            var eventId = Guid.NewGuid().ToString();
            var commandId = Guid.NewGuid().ToString();
            vault.Add(new StoredRemoteConnection(
                new RemoteConnectionInfo(Guid.NewGuid().ToString(), "https://one.example", AgentId, DateTimeOffset.UtcNow,
                    null, null, RemoteConnectionState.Connected, null),
                "access-token-one", "refresh-token-one", DateTimeOffset.UtcNow.AddMinutes(10),
                DateTimeOffset.UtcNow.AddDays(10), new RemoteSnapshotEnvelope(deliveryId, AgentId, snapshot),
                [new RemoteChangeEnvelope(eventId, AgentId, new StorageChange(StorageChangeKind.HealthChanged,
                    "disk-id", StorageHealth.Healthy, StorageHealth.Degraded, DateTimeOffset.UtcNow))], [],
                [new RemoteCommandResult(commandId, true, null, snapshot, null, DateTimeOffset.UtcNow)]));

            var storedFile = File.ReadAllText(path);
            Assert.DoesNotContain("access-token-one", storedFile, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-token-one", storedFile, StringComparison.Ordinal);
            Assert.DoesNotContain(deliveryId, storedFile, StringComparison.Ordinal);
            Assert.Empty(vault.List().SelectMany(static info =>
                new[] { info.ConnectionId, info.AgentId, info.ServerUrl }.Where(static value => value.Contains("token", StringComparison.OrdinalIgnoreCase))));
            var recovered = vault.ReadAll().Single();
            Assert.Equal("refresh-token-one", recovered.RefreshToken);
            Assert.Equal(deliveryId, recovered.PendingSnapshot?.DeliveryId);
            Assert.Equal(eventId, recovered.PendingEvents.Single().EventId);
            Assert.Equal(commandId, Assert.Single(recovered.PendingCommandResults!).CommandId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReplayedCommandsAreNotExecutedWhileTheirResultIsPendingOrAlreadyAcknowledged()
    {
        var processedCommandId = Guid.NewGuid().ToString();
        var pendingCommandId = Guid.NewGuid().ToString();
        var connection = new StoredRemoteConnection(
            new RemoteConnectionInfo(Guid.NewGuid().ToString(), "https://one.example", AgentId,
                DateTimeOffset.UtcNow, null, null, RemoteConnectionState.Connected, null),
            "access-token-which-is-at-least-thirty-two-characters",
            "refresh-token-which-is-at-least-thirty-two-characters",
            DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddDays(30),
            null, [], [processedCommandId],
            [new RemoteCommandResult(pendingCommandId, true, null, CreateSnapshot(), null, DateTimeOffset.UtcNow)]);

        Assert.False(RemoteCommandReplayGuard.ShouldExecute(connection, processedCommandId));
        Assert.False(RemoteCommandReplayGuard.ShouldExecute(connection, pendingCommandId));
        Assert.True(RemoteCommandReplayGuard.ShouldExecute(connection, Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task RefreshReusesPersistedRequestIdAfterServiceRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storage-agent-vault-{Guid.NewGuid():N}.bin");
        var requestId = Guid.NewGuid().ToString();
        try
        {
            var vault = new RemoteConnectionVault(new TestSecretProtector(), path);
            var connection = new StoredRemoteConnection(
                new RemoteConnectionInfo(Guid.NewGuid().ToString(), "https://one.example", AgentId,
                    DateTimeOffset.UtcNow, null, null, RemoteConnectionState.Connected, null),
                "access-token-which-is-at-least-thirty-two-characters",
                "refresh-token-which-is-at-least-thirty-two-characters",
                DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddDays(30),
                null, [], [], null, requestId);
            vault.Add(connection);

            string? sentRequestId = null;
            var handler = new DelegateHandler(async (request, token) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                sentRequestId = body.RootElement.GetProperty("requestId").GetString();
                var now = DateTimeOffset.UtcNow;
                return JsonResponse(new RemoteTokenRefreshResponse(
                    "refreshed-access-token-which-is-at-least-thirty-two-characters",
                    "refreshed-refresh-token-which-is-at-least-thirty-two-characters",
                    now.AddMinutes(15), now.AddDays(30)));
            });
            var remote = new RemoteProtocolClient(new HttpClient(handler), vault);

            var refreshed = await remote.EnsureTokenAsync(connection, CancellationToken.None);

            Assert.Equal(requestId, sentRequestId);
            Assert.Null(vault.Find(connection.Info.ConnectionId)!.PendingRefreshRequestId);
            Assert.Equal("refreshed-refresh-token-which-is-at-least-thirty-two-characters", refreshed.RefreshToken);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void VaultRejectsOverflowInsteadOfDroppingPendingCommandResults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storage-agent-vault-{Guid.NewGuid():N}.bin");
        try
        {
            var vault = new RemoteConnectionVault(new TestSecretProtector(), path);
            var connectionId = Guid.NewGuid().ToString();
            vault.Add(new StoredRemoteConnection(
                new RemoteConnectionInfo(connectionId, "https://one.example", AgentId,
                    DateTimeOffset.UtcNow, null, null, RemoteConnectionState.Connected, null),
                "access-token-which-is-at-least-thirty-two-characters",
                "refresh-token-which-is-at-least-thirty-two-characters",
                DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddDays(30), null, [], []));

            var results = Enumerable.Range(0, 9)
                .Select(_ => new RemoteCommandResult(Guid.NewGuid().ToString(), false,
                    "diagnostic.collection_failed", null, null, DateTimeOffset.UtcNow))
                .ToArray();
            Assert.Throws<InvalidOperationException>(() => vault.Update(connectionId,
                current => current with { PendingCommandResults = results }));
            Assert.Empty(vault.Find(connectionId)!.PendingCommandResults ?? []);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task LocalSnapshotIsEncryptedAtRestAndRestored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storage-agent-snapshot-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new StorageSnapshotStore(new TestSecretProtector(), path);
            var snapshot = new StorageSnapshot(StorageProtocol.Major, StorageProtocol.Minor, DateTimeOffset.UtcNow,
                [], [], [new StorageDiagnostic("private-diagnostic", "detail", "test", DateTimeOffset.UtcNow)]);
            await store.WriteAsync(snapshot, CancellationToken.None);
            Assert.DoesNotContain("private-diagnostic", File.ReadAllText(path), StringComparison.Ordinal);
            var restored = await store.ReadAsync(CancellationToken.None);
            Assert.Equal("private-diagnostic", Assert.Single(restored!.Diagnostics).Code);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task EnrollmentUsesConfiguredProtocolRootAndRetriesWithSameIdempotencyKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storage-agent-vault-{Guid.NewGuid():N}.bin");
        try
        {
            var vault = new RemoteConnectionVault(new TestSecretProtector(), path);
            var requests = new List<(string Url, string Body)>();
            var attempt = 0;
            var handler = new DelegateHandler(async (request, token) =>
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(token);
                requests.Add((request.RequestUri!.AbsoluteUri, body));
                if (Interlocked.Increment(ref attempt) == 1)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var now = DateTimeOffset.UtcNow;
                return JsonResponse(new RemoteEnrollmentResponse(Guid.NewGuid().ToString(), AgentId,
                    "access-token-which-is-at-least-thirty-two-characters", "refresh-token-which-is-at-least-thirty-two-characters",
                    now.AddMinutes(5), now.AddDays(30)));
            });
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var remote = new RemoteProtocolClient(http, vault);
            var connection = await remote.EnrollAsync("https://server.example/root/api/storage-agent/v1", "one-time-code", AgentId, "1.0.0", CancellationToken.None);

            Assert.Equal(2, requests.Count);
            Assert.All(requests, static request => Assert.StartsWith("https://server.example/root/api/storage-agent/v1/agents/enroll", request.Url, StringComparison.Ordinal));
            using var first = JsonDocument.Parse(requests[0].Body);
            using var second = JsonDocument.Parse(requests[1].Body);
            Assert.Equal(first.RootElement.GetProperty("requestId").GetString(), second.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("one-time-code", second.RootElement.GetProperty("enrollmentCode").GetString());
            Assert.True(Guid.TryParse(second.RootElement.GetProperty("requestId").GetString(), out _));
            Assert.Equal(RemoteConnectionState.Connected, connection.Info.State);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("http://server.example")]
    [InlineData("https://user:password@server.example")]
    [InlineData("https://server.example/?secret=value")]
    [InlineData("https://localhost")]
    [InlineData("https://LOCALHOST.")]
    [InlineData("https://agent.localhost")]
    public async Task EnrollmentRejectsInsecureOrAmbiguousServerUrls(string serverUrl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"storage-agent-vault-{Guid.NewGuid():N}.bin");
        try
        {
            var vault = new RemoteConnectionVault(new TestSecretProtector(), path);
            var remote = new RemoteProtocolClient(new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))), vault);
            await Assert.ThrowsAsync<RemoteProtocolException>(() => remote.EnrollAsync(serverUrl, "code", "agent", "1.0.0", CancellationToken.None));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static StorageSnapshot CreateSnapshot() => new(StorageProtocol.Major, StorageProtocol.Minor,
        DateTimeOffset.UtcNow, [], [], []);

    private const string AgentId = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, StorageContractJson.Options), Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        private const byte Mask = 0xA7;
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Select(static value => (byte)(value ^ Mask)).ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray().Select(static value => (byte)(value ^ Mask)).ToArray();
    }
}
