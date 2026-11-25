using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StorageAgent.Contracts;

namespace StorageAgent.Service;

internal sealed class RemoteProtocolException(string code, string message, bool retryable = false, TimeSpan? retryAfter = null) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>HTTPS-only implementation of the public Storage Agent server protocol.</summary>
internal sealed class RemoteProtocolClient(HttpClient httpClient, RemoteConnectionVault vault)
{
    private const int MaximumResponseBytes = StorageProtocol.MaxFrameBytes;
    private static readonly JsonSerializerOptions Json = StorageContractJson.Options;
    private static readonly Regex AgentIdPattern = new("^[A-F0-9]{40}$", RegexOptions.CultureInvariant);

    public async Task<StoredRemoteConnection> EnrollAsync(string serverUrl, string enrollmentCode,
        string agentId, string agentVersion, CancellationToken token)
    {
        var server = ValidateServer(serverUrl);
        if (string.IsNullOrWhiteSpace(enrollmentCode) || enrollmentCode.Length > 256)
            throw new RemoteProtocolException("enrollment.code_invalid", "The one-time enrollment code is invalid.");
        if (!AgentIdPattern.IsMatch(agentId))
            throw new RemoteProtocolException("enrollment.agent_id_invalid", "The machine identity is invalid.");

        var requestId = Guid.NewGuid().ToString();
        RemoteEnrollmentResponse response;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                response = await SendJsonNoAuthAsync<RemoteEnrollmentResponse>(server,
                    "agents/enroll", HttpMethod.Post,
                    new RemoteEnrollmentRequest(requestId, enrollmentCode, agentId, StorageProtocol.Major,
                        StorageProtocol.Minor, agentVersion), token);
                break;
            }
            catch (RemoteProtocolException error) when (error.Retryable && attempt < 4)
            {
                var delay = error.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));
                await Task.Delay(delay, token);
            }
        }
        ValidateTokenResponse(response.AccessToken, response.RefreshToken, response.AccessTokenExpiresAt,
            response.RefreshTokenExpiresAt);
        if (!Guid.TryParse(response.ConnectionId, out _) ||
            !string.Equals(response.AgentId, agentId, StringComparison.Ordinal))
            throw new RemoteProtocolException("enrollment.response_invalid", "The server returned an invalid enrollment response.");

        var info = new RemoteConnectionInfo(response.ConnectionId, server.AbsoluteUri.TrimEnd('/'), agentId,
            DateTimeOffset.UtcNow, null, null, RemoteConnectionState.Connected, null);
        return new StoredRemoteConnection(info, response.AccessToken, response.RefreshToken,
            response.AccessTokenExpiresAt, response.RefreshTokenExpiresAt, null, [], []);
    }

    public async Task RevokeAsync(StoredRemoteConnection connection, CancellationToken token)
    {
        var payload = new RemoteTokenRefreshRequest(Guid.NewGuid().ToString(), connection.RefreshToken);
        using var request = CreateJsonRequest(connection.Info.ServerUrl, "tokens/revoke", HttpMethod.Post, payload);
        using var response = await SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            return;
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        throw ResponseError(response);
    }

    public async Task<StoredRemoteConnection> EnsureTokenAsync(StoredRemoteConnection connection,
        CancellationToken token)
    {
        if (connection.Info.State != RemoteConnectionState.Connected)
            throw new RemoteProtocolException("auth.reauthorization_required", "This server connection needs to be authorized again.");
        if (connection.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return connection;
        return await RefreshTokenAsync(connection, token);
    }

    public async Task SendSnapshotAsync(StoredRemoteConnection connection, RemoteSnapshotEnvelope envelope,
        CancellationToken token)
    {
        var path = $"agents/{Uri.EscapeDataString(connection.Info.AgentId)}/snapshots/{Uri.EscapeDataString(envelope.DeliveryId)}";
        using var request = CreateJsonRequest(connection.Info.ServerUrl, path, HttpMethod.Put, envelope);
        using var response = await SendAuthenticatedAsync(connection, request, token);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.NoContent))
            throw ResponseError(response);
    }

    public async Task<IReadOnlyList<string>> SendEventsAsync(StoredRemoteConnection connection,
        RemoteChangeBatch batch, CancellationToken token)
    {
        var path = $"agents/{Uri.EscapeDataString(connection.Info.AgentId)}/events";
        var chunk = batch with { Events = batch.Events.Take(100).ToArray() };
        using var request = CreateJsonRequest(connection.Info.ServerUrl, path, HttpMethod.Post, chunk);
        using var response = await SendAuthenticatedAsync(connection, request, token);
        if (!response.IsSuccessStatusCode) throw ResponseError(response);
        if (response.StatusCode == HttpStatusCode.NoContent) return chunk.Events.Select(static item => item.EventId).ToArray();
        var acknowledgement = await ReadJsonAsync<RemoteAcknowledgement>(response, token);
        return acknowledgement.AcceptedEventIds;
    }

    public async Task<IReadOnlyList<RemoteCommand>> PollCommandsAsync(StoredRemoteConnection connection,
        CancellationToken token)
    {
        var path = $"agents/{Uri.EscapeDataString(connection.Info.AgentId)}/commands?waitSeconds=5";
        using var request = CreateJsonRequest<object?>(connection.Info.ServerUrl, path, HttpMethod.Get, null);
        using var response = await SendAuthenticatedAsync(connection, request, token);
        if (response.StatusCode == HttpStatusCode.NoContent) return [];
        if (!response.IsSuccessStatusCode) throw ResponseError(response);
        var batch = await ReadJsonAsync<RemoteCommandBatch>(response, token);
        return batch.Commands.Take(32).ToArray();
    }

    public async Task SendCommandResultAsync(StoredRemoteConnection connection, RemoteCommandResult result,
        CancellationToken token)
    {
        var path = $"agents/{Uri.EscapeDataString(connection.Info.AgentId)}/commands/{Uri.EscapeDataString(result.CommandId)}/result";
        using var request = CreateJsonRequest(connection.Info.ServerUrl, path, HttpMethod.Put, result);
        using var response = await SendAuthenticatedAsync(connection, request, token);
        if (!response.IsSuccessStatusCode) throw ResponseError(response);
    }

    private async Task<StoredRemoteConnection> RefreshTokenAsync(StoredRemoteConnection connection,
        CancellationToken token)
    {
        var requestId = connection.PendingRefreshRequestId ?? Guid.NewGuid().ToString();
        vault.Update(connection.Info.ConnectionId, current => current with
        {
            PendingRefreshRequestId = current.PendingRefreshRequestId ?? requestId
        });
        connection = vault.Find(connection.Info.ConnectionId) ?? connection;
        requestId = connection.PendingRefreshRequestId ?? requestId;
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateJsonRequest(connection.Info.ServerUrl, "tokens/refresh", HttpMethod.Post,
                new RemoteTokenRefreshRequest(requestId, connection.RefreshToken));
            try { response = await SendAsync(request, token); }
            catch (RemoteProtocolException error) when (error.Retryable && attempt < 4)
            {
                var delay = error.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));
                await Task.Delay(delay, token);
                continue;
            }
            break;
        }
        using (response)
        {
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                vault.Update(connection.Info.ConnectionId, current => current with
                {
                    Info = current.Info with { State = RemoteConnectionState.ReauthorizationRequired, LastError = "auth.reauthorization_required" }
                });
            throw ResponseError(response);
        }
        var refreshed = await ReadJsonAsync<RemoteTokenRefreshResponse>(response, token);
        ValidateTokenResponse(refreshed.AccessToken, refreshed.RefreshToken, refreshed.AccessTokenExpiresAt,
            refreshed.RefreshTokenExpiresAt);
        var next = connection with
        {
            AccessToken = refreshed.AccessToken,
            RefreshToken = refreshed.RefreshToken,
            AccessTokenExpiresAt = refreshed.AccessTokenExpiresAt,
            RefreshTokenExpiresAt = refreshed.RefreshTokenExpiresAt,
            PendingRefreshRequestId = null,
            Info = connection.Info with { State = RemoteConnectionState.Connected, LastError = null }
        };
        vault.Update(connection.Info.ConnectionId, _ => next);
        return next;
        }
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(StoredRemoteConnection connection,
        HttpRequestMessage request, CancellationToken token)
    {
        var current = await EnsureTokenAsync(connection, token);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);
        var response = await SendAsync(request, token);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        current = await RefreshTokenAsync(current, token);
        using var retry = CloneRequest(request, current.AccessToken);
        return await SendAsync(retry, token);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, string accessToken)
    {
        var request = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (source.Content is not null)
        {
            var bytes = source.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            request.Content = new ZeroingByteArrayContent(bytes);
            foreach (var header in source.Content.Headers) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return request;
    }

    private async Task<T> SendJsonNoAuthAsync<T>(Uri server, string path, HttpMethod method,
        object payload, CancellationToken token)
    {
        using var request = CreateJsonRequest(server.AbsoluteUri, path, method, payload);
        using var response = await SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw ResponseError(response);
        return await ReadJsonAsync<T>(response, token);
    }

    private static HttpRequestMessage CreateJsonRequest<T>(string serverUrl, string path, HttpMethod method, T? payload)
    {
        var server = ValidateServer(serverUrl);
        var endpoint = new Uri(server, path);
        if (!string.Equals(endpoint.Host, server.Host, StringComparison.OrdinalIgnoreCase) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new RemoteProtocolException("server.endpoint_invalid", "The server protocol endpoint is invalid.");
        var request = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            if (bytes.Length > MaximumResponseBytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new RemoteProtocolException("request.payload_too_large", "The protocol request exceeds its configured size limit.");
            }
            request.Content = new ZeroingByteArrayContent(bytes);
        }
        if (request.Content is not null) request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new RemoteProtocolException("server.timeout", "The remote server did not respond in time.", retryable: true); }
        catch (HttpRequestException)
        { throw new RemoteProtocolException("server.unavailable", "The remote server is unavailable.", retryable: true); }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new RemoteProtocolException("server.response_too_large", "The remote server response exceeds the protocol limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (memory.Length + read > MaximumResponseBytes)
                throw new RemoteProtocolException("server.response_too_large", "The remote server response exceeds the protocol limit.");
            memory.Write(buffer, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(memory.ToArray(), Json)
                ?? throw new RemoteProtocolException("server.response_invalid", "The remote server returned an invalid response.");
        }
        catch (JsonException)
        { throw new RemoteProtocolException("server.response_invalid", "The remote server returned an invalid response."); }
    }

    private static Uri ValidateServer(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
            throw new RemoteProtocolException("server.url_invalid", "The server address must be an HTTPS origin or base path without credentials, query, or fragment.");
        if (IPAddress.TryParse(server.Host, out var address) && IPAddress.IsLoopback(address))
            throw new RemoteProtocolException("server.url_invalid", "Loopback server addresses are not supported for remote connections.");
        var normalizedHost = server.IdnHost.TrimEnd('.');
        if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new RemoteProtocolException("server.url_invalid", "Loopback server addresses are not supported for remote connections.");
        return new Uri(server.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static void ValidateTokenResponse(string accessToken, string refreshToken,
        DateTimeOffset accessExpiresAt, DateTimeOffset refreshExpiresAt)
    {
        if (accessToken.Length is < 32 or > 4096 ||
            refreshToken.Length is < 32 or > 4096 ||
            accessExpiresAt <= DateTimeOffset.UtcNow || refreshExpiresAt <= accessExpiresAt)
            throw new RemoteProtocolException("auth.response_invalid", "The server returned invalid or expired credentials.");
    }

    private static RemoteProtocolException ResponseError(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new RemoteProtocolException("auth.rejected", "The server rejected this agent connection.");
        if (status == HttpStatusCode.TooManyRequests)
            return new RemoteProtocolException("server.rate_limited", "The remote server is rate limiting this agent.", retryable: true,
                retryAfter: response.Headers.RetryAfter?.Delta);
        return status switch
        {
            HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                => new RemoteProtocolException("server.unavailable", "The remote server is temporarily unavailable.", retryable: true),
            _ when (int)status >= 500 => new RemoteProtocolException("server.error", "The remote server returned a temporary error.", retryable: true),
            _ => new RemoteProtocolException($"server.http_{(int)status}", "The remote server rejected this protocol request.")
        };
    }

    private sealed record RemoteAcknowledgement(IReadOnlyList<string> AcceptedEventIds);

    private sealed class ZeroingByteArrayContent : ByteArrayContent
    {
        private readonly byte[] _bytes;
        public ZeroingByteArrayContent(byte[] bytes) : base(bytes) => _bytes = bytes;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) CryptographicOperations.ZeroMemory(_bytes);
        }
    }
}
