# Architecture

SqlBackup consists of a WPF desktop, a Windows background service and a setup helper. Desktop management uses an authenticated local named pipe restricted to elevated administrators and SYSTEM. There is no network management API.

## Components

- Core defines contracts, path and configuration validation, scheduling and IPC client behavior.
- Infrastructure implements SQLite storage, DPAPI secrets, SQL operations, verified transfers and retention planning.
- Service hosts management requests, persisted scheduling and the bounded serial execution queue.
- Desktop provides job configuration, inspection, history, diagnostics and local notifications.
- Setup manages the owned Windows service, maintenance, upgrades, recovery snapshots and uninstall.
- Tests is an executable verification suite with optional isolated SQL and installer checks.

## Data and identities

The service uses `%ProgramData%\SqlBackup\configuration.db`. Jobs own independent SQL and storage credentials. DPAPI CurrentUser protection binds secrets to the Windows service identity; installed deployments use LocalSystem. Queued runs keep immutable configuration and protected credential snapshots. Secrets are separate from ordinary job/history JSON and are never returned to the desktop.

The host holds an exclusive data-directory lock. SQLite records job revisions, schedule cursors, run evidence, observed events, notifications and cleanup audit. Schema upgrades preserve historical snapshots; older binaries reject newer schemas.

## Execution

Scheduling and desktop previews share the same calculator. Persisted occurrence keys prevent duplicate enqueueing. The service serializes work and uses SQL application locks for per-database coordination. Closing the desktop does not cancel backups. Interrupted runs remain visible and SQL backup commands are not blindly retried.

SQL writes to its configured temporary folder under the SQL service identity. AbaSqlBackup reads that file, verifies transfers through partial files and no-overwrite promotion, and persists final-copy evidence before temporary cleanup. Storage roles can use independent UNC credentials. See [staging and NAS](staging-and-nas.md).

Retention uses recorded ownership, integrity and backup-chain dependencies. Missing or uncertain evidence blocks deletion. Cleanup records intent and outcome without erasing historical metadata.

## Operations

History uses bounded service-side paging. Database selection loads asynchronously, supports cancellation and can reuse a recent matching inspection. Job logs store observed transition timestamps. Local notifications are durable and require explicit read acknowledgements.

Maintenance blocks new work and configuration changes during upgrades. Setup preserves application and SQLite recovery copies, validates readiness under the installed identity and attempts application/database rollback on failure. See [installation and recovery](installation-and-recovery.md) for limits.
