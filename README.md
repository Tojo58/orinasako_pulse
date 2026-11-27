# Orinasako Pulse

Orinasako Pulse is an MIT-licensed, standalone Windows service for read-only system monitoring, hardware inventory and native storage health diagnostics. It collects what the Windows storage stack and the hardware driver expose, keeps an encrypted local snapshot, and can optionally send it to one or more compatible servers.

The agent works without an Internet connection or remote account. Remote connections are initiated by the service over HTTPS; Windows does not need an inbound firewall rule or listening TCP port. The service never runs a shell command or PowerShell for disk diagnostics.

## System monitoring

The existing service now includes independent Windows system collectors in addition to native SMART. Run `pulse system --json` from an elevated terminal to obtain the storage inventory and system telemetry in one JSON response. A remote server receives only storage by default; to opt into additional categories during pairing, use `pulse connect --server https://inventory.example/api/storage-agent/v1 --collectors cpu,memory,gpu`. To include storage as well, specify `--collectors storage,cpu,memory,gpu`. The backend must support the optional versioned `system` envelope property in OpenAPI before opting in. Existing connections remain storage-only; disconnect and re-enroll to change permissions. See [system monitoring](docs/system-monitoring.md).

## Components

- `Orinasako.Pulse.Service`: Windows service; collector scheduling, runtime state, named-pipe server and remote synchronization orchestration.
- `Orinasako.Pulse.Collectors`: independent CPU, memory, GPU, motherboard, network, power, thermal, OS, processes, peripherals and security collectors.
- `Orinasako.Pulse.Transport`: outbound HTTPS protocol, per-destination privacy filtering and DPAPI-protected queue.
- `Orinasako.Pulse.Client`: .NET SDK for local named-pipe consumers.
- `pulse`: CLI for pairing, status, synchronization, logs and disconnection.
- `Orinasako.Pulse.Installer`: install, update and uninstall operations for administrators.
- `Orinasako.Pulse.Contracts`: versioned local IPC and remote protocol DTOs.
- `Orinasako.Pulse.Core`: native Windows storage collection and protocol parsers.

No project requires a business application. The service uses the Windows Storage APIs and read-only ATA/NVMe/SCSI pass-through where the active driver and controller permit it. USB bridges, RAID controllers, virtual disks and vendor drivers can hide or translate SMART data; such metrics are reported unavailable instead of guessed.

## Build and test

Requires the .NET SDK selected by `global.json`.

```sh
dotnet restore Orinasako.Pulse.slnx
dotnet build Orinasako.Pulse.slnx --no-restore
dotnet test Orinasako.Pulse.slnx --no-build
```

These commands compile on Linux for static review and parser tests. The Windows service, DPAPI, named pipes, Service Control Manager installation, physical SMART collection and hardware compatibility must be validated on Windows. A passing cross-platform build does not claim hardware support.

## Install and use

Run the packaged installer as an elevated Windows administrator. Installation creates `OrinasakoPulse` as an automatic Windows service and protects its ProgramData with ACLs for SYSTEM and Administrators. Uninstall preserves the encrypted connection vault and identity data unless `--remove-data` is explicitly specified.

```text
pulse status
pulse connect --server https://inventory.example/api/storage-agent/v1
pulse connections list
pulse sync
pulse logs 100
pulse disconnect <connection-id>
```

`connect` asks for the temporary one-time code without echoing it or placing it in shell history/arguments. An administrator must approve connection changes. The agent stores per-server access/refresh credentials and pending deliveries with machine-scoped Windows DPAPI. Tokens are never included in the CLI output or logs.

## Remote protocol

Any backend can implement the versioned protocol using the [OpenAPI document](docs/openapi.yaml). The `--server` value is the exact HTTPS protocol root (conventionally `/api/storage-agent/v1`); it may be mounted at another path by the server. Enrollment codes are created by the server's authenticated administrative interface; the operator enters the code in the local CLI. The backend redeems the code once, issues a connection-scoped token pair and binds the pseudonymous agent ID to server-side authorization. The agent sends snapshots/events, retrieves only documented read-only diagnostic requests, rotates tokens and retries queued data after outages.

See [protocol and server integration](docs/protocol.md), [architecture](docs/architecture.md), [hardware support](docs/hardware-support.md), and the [ASP.NET minimal API integration example](examples/server-dotnet/README.md).

## Privacy

Raw disk serials and Windows paths are used only during local mapping. The local volume fingerprint is omitted entirely from remote telemetry. They are not included in shared protocol contracts. The remote identity is a machine-local HMAC pseudonym and each server receives its own connection ID and authorization. An administrator can inspect the public contract before pairing and can revoke a server connection from the local CLI.

## Standalone Windows setup

The optional NSIS script at `scripts/package.nsi` creates a self-contained setup from the published `artifacts/package/payload` and `artifacts/package/installer` directories. Pass `-DAPP_VERSION=<version>` matching `Directory.Build.props`. The setup invokes `ensure` and does not downgrade or remove an already shared engine. The consumer may independently authenticate a published setup with its own distribution trust policy. No application-specific server is required by the agent.

## Public Windows releases

Download the standalone installer from [GitHub Releases](https://github.com/Tojo58/orinasako_storage_agent/releases). See [release and Authenticode policy](docs/releases.md) for verification, automated publication and optional signing. No Orinasako account is required. An unsigned release is not a signed Windows publisher identity.
