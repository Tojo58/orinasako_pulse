# Hardware support and validation

Support is capability-based. Windows storage class drivers, controller firmware and bridges determine which IOCTLs and protocol pages are exposed. A successful enumeration does not mean that SMART, temperature or lifetime counters are available.

| Interface | Collection path | Typical limits |
| --- | --- | --- |
| ATA/SATA | Windows storage descriptors and read-only ATA SMART/IDENTIFY pass-through | RAID/HBA firmware may block pass-through; vendor raw-value units differ. |
| NVMe | Windows protocol-specific storage queries for Identify and SMART/Health log | OEM drivers may expose only a subset; vendor-specific logs are not guessed. |
| SCSI/SAS | Storage reliability counters and supported SCSI log pages | USB, RAID and virtual controllers may hide the device's real health data. |
| USB storage | Windows descriptors plus pass-through where the bridge supports it | Many bridges translate or reject ATA/NVMe SMART commands. |
| RAID/virtual | Windows Storage reliability counters when exposed | Physical member disks are not claimed unless Windows exposes them independently. |

Each field reports `available`, `unavailable`, `unsupported` or `error`, with its source. Health is `unknown` when no trustworthy status source is present. Vendor-specific ATA attributes are reported raw/normalized without assigning undocumented units. NVMe standardized health fields are interpreted according to the protocol.

Release qualification must record Windows edition/build, storage driver, controller and firmware, bus type, drive make/model/firmware, USB bridge or RAID/HBA model, available fields and exact error status. Test against known-good, warning and failing fixtures, while hardware failures are simulated in parser fixtures rather than induced on real disks. Validate Windows IOCTL behavior on actual hardware before advertising a combination as supported.

The current Linux/WSL build cannot validate physical Windows IOCTLs, DPAPI, service ACLs or controllers. Treat those as outstanding until Windows hardware validation is recorded.

## API references

- Microsoft documents [`IOCTL_STORAGE_QUERY_PROPERTY`](https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddstor/ni-ntddstor-ioctl_storage_query_property) and [`STORAGE_PROTOCOL_DATA_DESCRIPTOR`](https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-storage_protocol_data_descriptor) for device/adapter and ATA/NVMe protocol-specific queries.
- Microsoft documents [`IOCTL_ATA_PASS_THROUGH`](https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddscsi/ni-ntddscsi-ioctl_ata_pass_through) as requiring a handle with read/write access. Orinasako Pulse uses that access only for enumerated IDENTIFY and SMART read commands; it has no code path for ATA write, firmware, sanitize or format commands.
- Windows exposes supplemental counters through [`MSFT_StorageReliabilityCounter`](https://learn.microsoft.com/windows-hardware/drivers/storage/msft-storagereliabilitycounter).
- [CrystalDiskInfo](https://crystalmark.info/en/software/CrystalDiskInfo/) is a capability/UX reference for storage-health monitoring. Orinasako Pulse does not copy its implementation or source code.
