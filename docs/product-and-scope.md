# Scope and roadmap

SqlBackup manages Windows SQL Server backup jobs with service-owned scheduling, Full/Differential/Log execution, verification, Local/Network Folder/NAS transfers, reviewed retention, history and local alerts.

Jobs support a list of folder, Azure Blob and Amazon S3 destinations, with independent credentials and results. See [backup destinations](backup-destinations.md) for execution, retention and validation limits.

## Planned

- Additional storage providers, including Google Drive.
- Opt-in automatic retention cleanup after successful backups, preserving restore dependencies and audit evidence.

## Future scope review

A restore wizard, automated restore testing as a product feature, email notifications and central management are not currently implemented. Availability and timing are not promised.

Core features do not require payment or donations.
