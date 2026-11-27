# Orinasako Pulse contributor instructions

Orinasako Pulse is a generic, open-source Windows system and storage monitoring agent. Keep the service usable without any remote server and independent of any business application.

## Architecture and security

- `Orinasako.Pulse.Core` owns read-only Windows hardware collection; `Orinasako.Pulse.Service` owns scheduling, local IPC and outbound sync orchestration; `Orinasako.Pulse.Transport` owns HTTPS, remote privacy filtering and encrypted connection state; `Orinasako.Pulse.Client` is the local named-pipe SDK; `Orinasako.Pulse.Cli` is the administrative interface; `Orinasako.Pulse.Contracts` owns versioned transport contracts.
- `Orinasako.Pulse.Collectors` owns independent read-only system probes. New remote system categories must be explicitly authorized per connection and omitted by default; never transmit user names, paths, raw hardware identifiers, IP/MAC addresses, process command lines or secret values.
- Never add business-specific URLs, credentials, roles, module names, database schemas or application dependencies to this repository.
- Remote connections are optional, independently scoped per HTTPS server and initiated outbound by the Windows service. Do not add listening ports, arbitrary remote commands, shell execution or write access to storage devices.
- Enrollment codes are single-use and short-lived. Never place enrollment codes or bearer credentials in command arguments, logs, telemetry, exception messages or unencrypted files. Persist remote state through Windows DPAPI and preserve strict filesystem ACLs.
- Do not transmit raw serial numbers, hardware identifiers, Windows usernames or paths. Disk identifiers sent over the protocol must be pseudonymous. Validate and bound all IPC and HTTP data.
- Preserve idempotency, offline delivery, token rotation/revocation, server isolation and read-only diagnostic commands.
- Keep Windows-specific APIs behind OS checks and keep parsers, protocol models and reliability logic testable without physical disks.

## Development

- Use the pinned .NET SDK in `global.json`, `Orinasako.Pulse.slnx`, nullable reference types and warnings-as-errors.
- Run local builds and tests serially when they compete for disk or network resources. Add tests for security boundaries and protocol behavior.
- Do not trigger GitHub Actions, publish packages, create a release, push, connect to a remote device, or install the service unless explicitly requested.
- Keep the project MIT licensed. Verify all dependencies and copied code remain compatible with MIT.
