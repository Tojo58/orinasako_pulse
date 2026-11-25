# ASP.NET Core server example

Implement the endpoints in `../../docs/openapi.yaml` in the consuming backend. The server owns authentication, database transactions, enrollment authorization, token hashing, retention and user/resource permissions; this example does not add a second identity system.

Choose and publish an HTTPS protocol root, such as `https://inventory.example/api/storage-agent/v1`. The agent is configured with this exact root. Mounting the same protocol at a different path does not change the agent or wire contracts.

Recommended implementation outline:

```csharp
public sealed record CreateEnrollmentRequest(string ResourceRef, string? Label);

app.MapPost("/api/storage-agent/v1/enrollments", async (HttpContext context, IEnrollmentStore store) =>
{
    var operatorId = await context.RequireAuthorizedOperatorAsync();
    var request = await context.Request.ReadFromJsonAsync<CreateEnrollmentRequest>();
    var enrollment = await store.CreateSingleUseCodeAsync(operatorId, request!.ResourceRef, request.Label,
        expiresIn: TimeSpan.FromMinutes(5), minimumEntropyBits: 128);
    return Results.Created($"/api/storage-agent/v1/enrollments/{enrollment.Id}", enrollment.PublicView);
}).RequireAuthorization();

app.MapPost("/api/storage-agent/v1/agents/enroll", async (EnrollmentRequest request, IEnrollmentStore store) =>
{
    var grant = await store.RedeemOnceAsync(request.RequestId, request.EnrollmentCode,
        request.AgentId, request.ProtocolMajor, request.AgentVersion);
    return grant is null ? Results.Unauthorized() : Results.Ok(grant);
});

app.MapPut("/api/storage-agent/v1/agents/{agentId}/snapshots/{deliveryId}", async (
    string agentId, string deliveryId, SnapshotEnvelope payload, ClaimsPrincipal principal, IInventoryStore store) =>
{
    var agent = await principal.RequireAgentScopeAsync(agentId);
    await store.UpsertDeliveryOnceAsync(agent.ResourceId, agentId, deliveryId, payload);
    return Results.NoContent();
}).RequireAuthorization("storage-agent");
```

Use cryptographically generated codes and opaque tokens, store hashes, and implement transactional compare-and-set for redeem/rotate/revoke. Persist idempotency keys and responses long enough to cover transport retries. Validate schemas and maximum body sizes before persistence. Never trust an agent ID to grant access to another server resource. The snippets are illustrative and intentionally leave each application's authentication and data model to that server.
