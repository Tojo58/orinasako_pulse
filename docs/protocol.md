# Storage Agent Remote Protocol v1

The normative HTTP contract is `openapi.yaml` (OpenAPI 3.1.2). Its server URL is the protocol root, conventionally `https://host/api/storage-agent/v1/`; endpoint paths below are relative to that root. A compatible backend may mount the root at any HTTPS path and implement the protocol without importing agent code or knowing about a particular business application. The service is optional; local collection and named-pipe clients work with no configured server.

## Pairing and authorization

1. An authenticated server operator creates an enrollment from the server's own UI by calling `POST {protocolRoot}/enrollments` with an opaque server-owned `resourceRef`. The code is random, single-use, short-lived (recommended 5 minutes), stored server-side as a hash, bound to the operator's server-side authorization/scope, and rate-limited. The UI displays the code once and identifies the resource being authorized.
2. On the Windows machine, an administrator runs `storageagent connect --server https://host/api/storage-agent/v1`. The URL is the exact protocol root; it must be HTTPS and contain no credentials, query, or fragment. The CLI reads the code without echo; the code is sent only to the service over its ACL-protected local named pipe, then over HTTPS.
3. The service posts the code, its stable pseudonymous agent ID and protocol version to `POST {protocolRoot}/agents/enroll`. The server consumes the code once and returns a connection ID plus short-lived access and rotating refresh credentials. The request carries a recovery `requestId`; a retry with the same code, agent ID and request ID during the bounded recovery window returns credentials for the same connection and invalidates any earlier response that may have been lost. This lets the agent persist only the response it actually received without storing plaintext token responses on the server. Never allow code lookup or redemption to reveal whether another code exists.
4. The service stores credentials and that server's pending queue in a DPAPI-protected local vault. Every server receives a different token pair; one server cannot see another server's connection, queue or status.

Servers should require codes with at least 128 bits of entropy, expire them quickly, accept each code once, bind code to the authorized resource and scope, and rate-limit creation and redemption. Display a human-readable machine identity only after enrollment and require explicit approval before linking it to an inventory record. Do not use a device-provided hostname as proof of identity.

## Authentication, rotation and revocation

The access token is sent as `Authorization: Bearer` over TLS only. Use opaque, cryptographically random credentials; access tokens should be short-lived, and refresh tokens should rotate on every successful refresh. Before sending a refresh request, the agent persists its request ID in the DPAPI vault and reuses it with the same previous token until it has persisted the returned pair. This recovers a lost response even if the Windows service restarts between rotation and receipt. The previous refresh token may be accepted only with the same request ID during a short window, and each retry returns a fresh active pair while invalidating the unconfirmed pair. Store only hashes of tokens server-side.

`POST {protocolRoot}/tokens/revoke` is idempotent and revokes the refresh grant and its access tokens. Local disconnect stops future data sends immediately. If the server is unreachable, the agent keeps only the encrypted revocation record and retries; it does not continue telemetry upload. The UI should also allow server-side revocation at any time.

## Synchronization and delivery

- `PUT {protocolRoot}/agents/{agentId}/snapshots/{deliveryId}` accepts a complete versioned inventory and is idempotent by `deliveryId`. The latest unsent snapshot replaces older queued snapshots.
- `POST {protocolRoot}/agents/{agentId}/events` accepts up to 100 device/health events per request, each with a stable event ID. Return accepted event IDs. Repeated IDs must not create duplicate history.
- The agent synchronizes immediately after pairing or a local `sync` request, on hardware/health changes, and periodically. Offline work remains queued and resumes with bounded exponential backoff. Honor HTTP `Retry-After` for 429 responses.
- The backend validates the bearer scope, agent ID, JSON size, protocol major version, data bounds and resource authorization on every request. Remote volume snapshots must leave mount paths empty and labels null; local names and paths stay on the machine. A successful HTTP response acknowledges only the corresponding delivery IDs.

## Read-only remote commands

`GET {protocolRoot}/agents/{agentId}/commands?waitSeconds=5` may wait at most 10 seconds and return only `refreshInventory` or `readDiskDiagnostics`. Commands have unique IDs and an expiry. Agent results are idempotently submitted to `{protocolRoot}/agents/{agentId}/commands/{commandId}/result`. Requests outside the enum are rejected. There is no shell, PowerShell, arbitrary process, filesystem write, disk write or configuration command.

## Errors and compatibility

Use the HTTP status for transport semantics and a stable JSON `code` for machine-readable errors. Never return tokens, enrollment codes, raw serials or Windows paths in errors. The service distinguishes invalid server URL, expired/rejected code, rate limit, revoked credential, unsupported protocol, unavailable server and malformed response. Major versions are breaking; minor versions add optional fields/operations only. Servers reject unsupported major versions explicitly.

## Server implementation checklist

- Authenticate the server-side enrollment UI using the server's existing user/session system and authorize it for the resource being linked.
- Store hashed enrollment codes and credentials; enforce expiry, one-use, request recovery scope, revocation and rate limits. A repeated recovery request must leave only its last returned credential pair active.
- Accept inventory/event IDs idempotently, validate payload schemas, and retain history according to the server's own retention policy.
- Poll/lease commands transactionally, allow only the documented read-only enum, expire unclaimed commands, and accept idempotent results.
- Keep database transactions and tenant/resource boundaries server-owned. Do not treat a machine identifier as a user identity.

See the OpenAPI spec and `examples/server-dotnet/README.md` for an example receiver.

## Local consumer permissions

Authenticated local users may read status, the cached inventory and diagnostics, resolve paths and subscribe to device changes without elevation. Fresh hardware collection, system reports, remote connection administration, remote synchronization and service logs require an elevated administrator. Local volume labels and mount paths are not sent to remote servers.

The local volume contract may expose a `localMountFingerprint` (SHA-256 of a normalized Windows volume GUID path, domain-separated by `volume:`) for consumers to verify a mount immediately before a filesystem operation. It is never included in remote telemetry. Local FireWire, SD and MMC bus distinctions map to `other` for the existing remote v1 wire schema.

## Local filesystem monitoring (1.4)

Administrative local IPC only: configure named consumers with up to 32 absolute roots (64 total), query root status, page immutable revisions, read bounded journal pages using a per-root cursor, and request reconciliation. The SQLite journal retains 10,000 events per root and survives restart; an expired cursor requires a snapshot reset. Ordinary IPC users cannot configure or read file inventories. No filesystem data is transmitted by the remote protocol. The worker uses the active Windows user for local and SMB reads; no active session or access failure marks the inventory incomplete and preserves the previous complete baseline. The watcher triggers reconciliation; NTFS USN records are read with a persisted journal ID/USN cursor and bounded recovery. The worker supplies hashed file references; only known, previously inventoried directory references can be resolved into paths. Journal gaps, unsupported record versions and retention overflow require snapshot reset. Periodic reconciliation covers FAT/exFAT, SMB and restart. Recovery is best-effort for unknown directories and rename pairs split across checkpoints, and transient changes can be missed without an available journal. Unique local file identities support rename/move inference; ambiguous or cross-volume moves are create/delete. Reparse points are not followed. The engine never modifies monitored files.

Local root identity and active Windows principal are bound to the baseline. A different principal or replacement volume is not allowed to erase that baseline; an administrator must explicitly unregister and re-register to change ownership. USN records are read-only and never transferred remotely. See Microsoft's FSCTL_READ_USN_JOURNAL and Walking a Buffer of Change Journal Records documentation.
