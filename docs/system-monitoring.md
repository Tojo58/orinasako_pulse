# Modular Windows system monitoring

The existing storage collector, Windows service identity, IPC pipe and HTTPS
storage protocol are preserved. StorageAgent.Collectors adds independently
scheduled, independently defined, allowlisted WMI probes for CPU, memory, GPU, motherboard/BIOS,
network, power, thermal, operating system, processes/services, peripherals and
security. Storage uses its native SMART collector and existing snapshot cache.
Each section and probe has its own observation time, probe status and per-field source/unit. Slowly changing inventory probes are cached for five minutes.
Unavailable values are JSON null, not zero. The schema is versioned separately
from the stable storage contract.

Run `storageagent system --json` as an administrator. Applications can call
`StorageAgentClient.GetSystemReportAsync` through the same administrator-gated
pipe. Configure intervals using .NET configuration, e.g.
`Monitoring__Intervals__Cpu=10` in the service environment, and restart the service.
Disable a category using e.g. `Monitoring__Enabled__Gpu=false`. Intervals are bounded to 5–86400 seconds; a blocked WMI provider cannot spawn
another instance of its own worker. The storage schedule is unchanged.

## Remote delivery

Pair using `storageagent connect --server https://inventory.example/api/storage-agent/v1 --collectors cpu,memory`.
The administrator enters the one-use code securely. Categories are stored in
the existing encrypted connection vault. Existing connections and ordinary
pairing remain storage-only. Explicit `--collectors` replaces the default scope: add `storage` to include disk diagnostics. Without it, no physical disk or volume data, storage events or remote storage commands are allowed. The snapshot envelope contains empty storage arrays and `storageIncluded:false`. To change scope, disconnect/re-enroll.
The existing HTTPS snapshot endpoint receives optional `system` data (schema
version 1) filtered to the explicitly selected categories. Update the server
to accept the additive OpenAPI field before opting in. The existing remote
loop, credentials, retry and durable pending envelope are reused; collection
and transmission are independent. Configure the periodic remote cycle using `Monitoring__RemoteSyncSeconds` (30–86400 seconds, default 60); storage events can trigger an earlier cycle. Remote commands remain storage-only.

No IP/MAC addresses, usernames, process command lines, executable paths,
device instance IDs or serial numbers are queried by the new collectors.
Process/service and peripheral observations deliberately expose bounded
aggregate or class/state data, not names or paths. GPU engine and NIC rows
are anonymous, not stable per-device mappings. Security provider state codes
are raw; they are not a claim that a device is secure. Secure Boot is read from the Windows firmware-state registry value if reported.

## Validation and limitations

This implementation uses Windows WMI/CIM providers for system metrics. It
does not implement proprietary CPU/GPU sensor drivers, precise per-GPU VRAM
above the legacy WMI AdapterRAM limit, component power measurements, actual
fan RPM, detailed process inventory, local system time-series
history, or system-specific remote events/refresh commands. ACPI temperatures
are thermal-zone values, not necessarily CPU temperatures; reported fan speed
is requested speed, not measured RPM. WMI classes and fields are hardware,
driver and edition dependent. Probes return at most 32 instances and report
truncation. No physical compatibility is asserted without Windows testing.
