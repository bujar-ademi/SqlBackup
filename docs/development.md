# Development

## Build

Use Windows with .NET SDK 10.0.401 or a later patch in that SDK band, as pinned by global.json. WPF requires Windows. Open SqlBackup.slnx in a compatible Visual Studio version or run:

```powershell
dotnet restore SqlBackup.slnx
dotnet build SqlBackup.slnx --no-restore
dotnet run --project tests/SqlBackup.Tests --no-build
dotnet format whitespace SqlBackup.slnx --verify-no-changes --no-restore
```

Tests is an executable, not a dotnet test project. The default suite uses temporary fixtures and simulated SQL; it does not install a service or contact a real SQL Server. DPAPI tests use the current Windows identity. Live IPC is skipped without elevation. Stop any other host before running the elevated `--require-ipc` suite.

For a headless WPF layout and dialog check:

```powershell
dotnet run --project tests/SqlBackup.Tests --no-build -- --render-desktop '.artifacts/desktop.png'
```

## Local operation

In an elevated terminal, run `dotnet run --project src/SqlBackup.Service --no-build`. Run the Desktop project from another elevated terminal. Console mode uses the interactive account; installed service mode uses LocalSystem. Never alternate identities against a populated data directory. Both use `%ProgramData%\SqlBackup`; read the [migration guide](installation-and-recovery.md) before installing over development data.

## Isolated SQL tests

Only use a fresh dedicated LocalDB instance whose name begins with AbaSqlBackupPhase2_. The suite creates synthetic databases and backup files, tests restore and retention, and removes its own databases. It retains backup fixtures for inspection.

```powershell
SqlLocalDB create AbaSqlBackupPhase2_ManualTest -s
dotnet run --project tests/SqlBackup.Tests --no-build -- --sql-instance '(localdb)\AbaSqlBackupPhase2_ManualTest'
SqlLocalDB stop AbaSqlBackupPhase2_ManualTest
SqlLocalDB delete AbaSqlBackupPhase2_ManualTest
```

## Packaging

Install Inno Setup 7 and use PowerShell 7 for packaging scripts:

```powershell
.\scripts\Build-Package.ps1 -Version '0.1.0'
```

Output is under `.artifacts/installer`: a self-contained win-x64 installer and SHA-256 sidecar. The compiler path can be supplied with -InnoCompiler. NuGet packages and the .NET runtime are restored during publishing; a pinned SDK is not a claim of byte-for-byte reproducibility. Package license inventories are generated from the resolved dependencies.

The Windows workflow builds, tests and packages. Version-tag pushes create a draft prerelease after the configured checks succeed. Branch builds do not create releases. Inspect the draft and evidence before public publication.

Artifact uploads explicitly allow the hidden `.artifacts` directory, restricted to the listed installer/checksum files and validation PNG/log patterns. Do not broaden these paths to configuration databases, credentials or real backup files.

`scripts/Test-Installer.ps1` is only for an empty disposable elevated Windows VM. It refuses an existing installation/data/service. Build Release tests and two successive installer versions, then explicitly pass -AllowInstallation outside GitHub Actions. It exercises installation, restart, synthetic rollback, upgrade and retained-data uninstall/reinstall. Discard the VM afterward. Never run this harness on a server hosting real jobs.

