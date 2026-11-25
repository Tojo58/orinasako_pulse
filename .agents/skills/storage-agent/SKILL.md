---
name: storage-agent
description: Develop the independent Windows Storage Agent, its SMART collectors, local SDK/CLI, generic remote protocol, packaging and server integration examples.
---

# Storage Agent

- Read `AGENTS.md` and the affected contract, client, service, documentation, tests and packaging files before editing.
- Keep collection native, asynchronous, bounded and read-only. Unsupported metrics must remain explicitly unavailable/unsupported; never infer healthy from missing data.
- Remote servers are optional generic consumers of the public protocol in `docs/openapi.yaml`. Keep the service's default mode fully local. Use outbound HTTPS only, one-use enrollment, per-server tokens, DPAPI-protected data, idempotent deliveries and a bounded encrypted offline queue.
- Remote work is restricted to inventory/SMART reporting and enumerated read-only diagnostic requests. Never implement remote shell, arbitrary command execution or disk writes.
- Keep hardware identifiers out of transport DTOs; add privacy and authorization tests whenever a contract or endpoint changes.
- Test native parsers with fixtures and protocol clients with fake HTTP handlers. Physical device, controller and Windows installer claims require corresponding Windows/hardware validation.
- Build with `dotnet build StorageAgent.slnx`; test with `dotnet test StorageAgent.slnx`. Do not run workflows, publish, push, or remotely install without explicit authorization.
