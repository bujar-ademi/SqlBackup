# SqlBackup

SQL Server backups, under your control. A Windows desktop application by [Bujar Ademi](https://bujarademi.com), with a background service that keeps working when the desktop is closed.

## Features

- Independent jobs with their own SQL connection, database selection and storage credentials.
- Manual and scheduled Full, Differential and Transaction Log backups.
- Editable SQL temporary folder, Local/Network Folder/NAS destinations and an optional secondary copy.
- RESTORE VERIFYONLY, SHA-256 verified transfers and separate backup, verification, copy and cleanup results.
- Recovery-aware retention previews with explicitly confirmed deletion.
- Compact execution history, job logs, diagnostics and local notifications.

## Get started

Use Windows 11 x64 or Windows Server 2022/2025 with Desktop Experience. The self-contained installer includes .NET 10 and requires administrator access. Build an installer using the [development guide](docs/development.md), or use a published release when available. Installers are currently unsigned.

After installation, create a job, configure SQL, select databases, and configure temporary/final folders before saving and running it. SQL Server must be able to access the temporary folder; the AbaSqlBackup service must be able to read that source and write the final destinations. For remote SQL, configure a source share exposing the same files.

See [installation and recovery](docs/installation-and-recovery.md), [job configuration](docs/windows-app.md), and the [documentation index](docs/README.md).

## Current limits

Cloud destinations and automatic retention deletion are planned. Retention cleanup currently requires preview and confirmation. The application does not include a restore wizard; VERIFYONLY is not a substitute for a tested restore. Keep independent copies and test your recovery procedure. Windows lifecycle and network behavior must be validated for your deployment.

## Development

```powershell
dotnet restore SqlBackup.slnx
dotnet build SqlBackup.slnx --no-restore
dotnet run --project tests/SqlBackup.Tests --no-build
```

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md) before submitting changes or security reports.

## License and support

Copyright 2026 Bujar Ademi. Licensed under [MIT](LICENSE). Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Core functionality has no payment or donation requirement.
