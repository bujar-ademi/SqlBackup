# Installation, upgrades and recovery

The installer is built with Inno Setup 7 and includes .NET 10 for win-x64. No separate .NET installation is required. The pilot installer is unsigned. The source is MIT licensed; third-party notices accompany the application.

## Installation

 documentation stays in the repository and is not included in new installers. About opens the GitHub docs folder. References below to prior Docs payloads describe legacy installations/recovery snapshots; no new documentation shortcut is installed.

Use a Windows 11 x64 or Windows Server 2022/2025 machine with Desktop Experience and an elevated administrator. Run `SqlBackup-Setup-<version>-win-x64.exe`; the fixed application directory is `%ProgramFiles%\SqlBackup`. A Start Menu shortcut is installed and a desktop shortcut is optional. Desktop management requests elevation, matching the existing local administrator authorization policy.

The installer registers `AbaSqlBackup` as LocalSystem with automatic startup. SCM restarts it after unexpected failures, with bounded delays; explicitly stopping it for setup is not treated as a crash. The application still marks unfinished backups Interrupted and does not automatically repeat SQL backup commands.

Operational state stays in `%ProgramData%\SqlBackup`, protected for SYSTEM and administrators. SQL/NAS credentials remain DPAPI CurrentUser ciphertext under LocalSystem. The installer never accepts or passes passwords through command lines. Configure per-job SQL-visible temporary folders and service-visible source mappings separately; setup never creates or deletes SQL/NAS backup folders.

## Existing development-console data

Setup refuses an existing unmanaged `configuration.db` or service without a matching installation identity record. Development-console ciphertext usually belongs to the interactive administrator, and changing to LocalSystem does not make it decryptable.

Do not delete or overwrite that store to bypass the check. First stop the console host and preserve a protected complete copy of its data directory. On an explicitly reviewed migration, retain the old store as an archive, install into a fresh machine-wide store and re-enter each job's SQL/NAS credentials under the installed service. The old archive retains historical evidence; the installer does not automatically merge or re-encrypt it. A migration involving real jobs/schedules requires separate environment authorization. Never run console and installed service against the same populated store with different identities.

## Upgrade

Close the desktop and wait for all queued/running backups to finish. Running the new installer checks the existing service path/account, rejects downgrades and refuses an unfinished prior transaction. Repair/reinstallation of the same numeric version is allowed.

The helper requests maintenance through authenticated local IPC. This checks the queue under its enqueue lock, durably blocks new runs/configuration changes and pauses schedule cursor advancement. It then stops the service and takes its exclusive host lock. The old managed program folders and an SQLite Backup API snapshot (including committed WAL state) are retained under `%ProgramData%\SqlBackup\UpgradeBackups\pending`. No ciphertext is decrypted during snapshotting.

After copying the new payload, setup starts the service with maintenance still active. It waits for store initialization and tests active SQL/NAS secret decryption under LocalSystem without contacting SQL or a NAS. Only then does it record the installed version and release maintenance. Previously enabled schedules resume using their existing missed-run policy. Failed setup attempts application/database rollback; failure to restore is reported and recovery evidence is retained. This is application rollback, not a claim of transactional Windows Installer/MSI registry rollback.

Completed/rolled-back snapshots are archived on the next preparation. They contain private configuration and ciphertext; retain/protect them deliberately. No automatic purge of recovery snapshots or old backup artifacts is included. Ensure adequate disk space for the published application plus a recovery copy.

## Interrupted or failed setup

An unresolved `setup-maintenance.json` is intentional: the service must not start SQL backups against a partially changed installation. Do not blindly remove it or start older binaries against a newer database schema. Keep the pending transaction, database and installation logs.

For a pending Prepared transaction, copy the complete installed `Setup` helper payload (currently one self-contained executable) to a separate administrator-only temporary directory. Recovery must run outside the managed program folders it replaces. In an elevated terminal, run:

```powershell
& 'C:\AdminRecovery\SqlBackup.Setup.exe' rollback `
    "$env:ProgramFiles\SqlBackup" '0.1.0' 'C:\AdminRecovery\result.txt'
```

Use the transaction's numeric target version for clarity; rollback uses the protected pending record. It restores managed application folders and validates/copies the database snapshot before replacing the current database. Original service running/stopped state is restored where applicable. If Windows uninstall metadata was affected by an interrupted installer, rerun the matching original installer to repair registration after application recovery. Do not use an older release to force a downgrade.

A Committed transaction is never rolled back automatically because schedules may already have resumed. Recovery of a committed release requires a reviewed, stopped-service restore of matching binaries/data, accounting for all runs since the snapshot. If a committed installation remains paused, inspect the cause and run the copied helper with `resume` only after confirming the installation is complete. A helper failure prints fixed actionable messages and an error type; raw credentials/SQL errors are not emitted.

## Uninstall and reinstall

Close the desktop and wait for work to finish, then uninstall through Windows Apps. Removal uses the same maintenance check, stops/removes only the owned service and removes installer-managed program files/shortcuts. It always retains `%ProgramData%\SqlBackup`, installation identity, jobs, protected credentials, execution history and recovery snapshots. All SQL temporary/final/NAS backup folders remain untouched.

Cancelling uninstall before removal resumes the installed service. Reinstall under the same LocalSystem identity can reuse the retained store and credentials; downgrades remain blocked. Removing retained state is a separate explicit administrative operation, never an installer default.

## Build and acceptance

```powershell
.\scripts\Build-Package.ps1 -Version '0.1.0'
```

Output: `.artifacts\installer\SqlBackup-Setup-0.1.0-win-x64.exe` and `.exe.sha256`. The script validates output paths before deleting only its known build-output subfolders. It publishes self-contained Desktop/Service and a self-contained single-file setup helper, then invokes Inno Setup 7. The compiler can be selected with `-InnoCompiler`.

`scripts/Test-Installer.ps1` is destructive to its own test installation and is exclusively for a clean disposable Windows VM. First build `SqlBackup.slnx --configuration Release` so its installed-service test executable is available, and build both the original and a newer installer. The harness requires administrator rights, an empty installation/data location and no existing AbaSqlBackup service. Outside GitHub Actions, explicitly pass `-AllowInstallation`. It tests clean install, service restart, rollback of synthetic file/schema changes, a newer installer, uninstall preserving state, and reinstall restoring access to fictional DPAPI credentials. Disabled synthetic jobs prevent SQL/NAS access. It leaves synthetic retained data for inspection; discard the VM afterward.

The hosted workflow runs these acceptance checks on Windows Server 2022. Local builds and pure maintenance/file tests do not establish that SCM installation or the actual workflow has passed. See release-checklist.md for publication checks and known verification limits.


