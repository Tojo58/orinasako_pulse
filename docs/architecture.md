# Architecture

## Local operation

The Windows service collects a bounded storage snapshot at startup, on device changes and periodically. Hardware requests use read-only Windows storage IOCTLs. The local snapshot and logs live under `%ProgramData%\StorageAgent`; the installer restricts the data directory to SYSTEM and Administrators. Collection remains available when all remote servers are offline.

Local consumers use the versioned named pipe and .NET client. The administrative CLI talks to the same service; connection changes require an elevated administrator token. The pipe rejects remote clients. The service does not open an inbound TCP port.

## System collectors

See [system monitoring](system-monitoring.md) for collector isolation, local JSON access, explicit remote category permissions and hardware limitations. Native storage retains its own scheduling and cache. System observations are sampled independently and opt-in telemetry is attached to the existing outbound snapshot envelope.

## Remote operation

Each remote server has an independent record, opaque access/refresh token pair, sync state and outbox. The service initiates HTTPS connections, does not follow redirects, validates server URLs, bounds response bodies, rotates credentials before expiry, and retries with exponential backoff plus `Retry-After` when provided.

Remote payloads are encrypted at rest using Windows DPAPI machine scope. The protected vault file is placed in the ACL-protected ProgramData directory. A pending inventory delivery retains its ID and payload until acknowledged; the next delivery uses the newest snapshot. System scopes are stored per connection and default to storage-only; health/device events are queued per server with a fixed upper bound and stable delivery IDs. If the event queue reaches capacity, new event loss is logged and surfaced in connection status while the latest snapshot remains available for reconciliation. Server endpoints must treat delivery and command IDs idempotently.

The server can request only `refreshInventory` or `readDiskDiagnostics`. The agent performs local read-only collection and returns typed data. No command can execute programs, invoke scripts, mutate files, change device settings or write to disks.

## Security boundaries

- A short-lived, single-use enrollment code links one local installation to one server. It is never supplied as a command-line option.
- Each server issues and revokes its own credentials. Disconnect disables upload immediately; if the server cannot be reached, revocation is persisted and retried while the other server connections continue independently.
- Server identity is the validated HTTPS origin/base path and normal Windows certificate-chain validation. Redirects and URL credentials are rejected.
- Hardware serials, raw identifiers, user names and file paths are not part of remote DTOs. Disk IDs are HMAC pseudonyms created locally.
- Missing telemetry is represented as unavailable, unsupported or error; it is never silently converted to a healthy value.

## Runtime limitations

Storage pass-through depends on Windows, storage class drivers, firmware, USB-to-SATA/NVMe bridges and RAID/HBA behavior. Some controllers suppress SMART data or expose only partial fields. Windows Storage reliability counters are supplemental and may also be absent. The agent reports actual capability per metric; compatibility must be validated on the target combination of Windows edition, driver, controller and device.
