# Per-job SQL staging and final storage

SQL temporary staging and final storage use independent paths and Windows identities.

This document describes the compatible primary/secondary folder workflow. Jobs edited through the new destination list use [multiple destinations](backup-destinations.md), including automatic local output selection and Azure/S3 delivery.

## Configuration and identities

New jobs configure `%ProgramData%\SqlBackup\Temp` as the fallback Temporary folder (resolved on the application host). Existing jobs keep their saved paths. Before enqueueing each run, the service automatically checks whether a staging job can write directly to its final folder. No storage-mode checkbox is required.

Direct output is selected only for a local fixed-drive destination without an explicit source mapping or source/final share credentials, when SQL reports both its machine and physical machine as this service computer, is not clustered, and can see the destination directory. Different drive letters do not require staging. UNC, remote SQL, mapped/removable drives, explicit source mappings and uncertain inspection results retain staging. Read-only inspection failure is logged and preserves the configured path; cancellation still cancels enqueueing. A SQL directory check does not prove write permission. A direct BACKUP failure is reported normally and is never retried into staging automatically.

The saved job remains unchanged. The run records the selected effective job/path and matching protected credentials before entering the queue, so transfers and retention use direct-artifact ownership for direct runs and final-copy evidence for staged runs. Secondary copying and VERIFYONLY remain unchanged. Direct runs have no primary transfer or staging cleanup. Historical runs are not rewritten.

Save & Close checks the SQL-visible output folder selected by the same policy. For staged output, the temporary folder must already exist and SQL Server's service account must have read/write access. For remote SQL choose a folder on that SQL host and configure its service-visible source mapping. The application does not create remote directories.

Store backups specifies the final folder visible to AbaSqlBackup, with one optional secondary folder. For a remote SQL host, Service-visible temporary folder must expose the same SQL-written files through an authorized UNC share, or run AbaSqlBackup on that SQL host. A local desktop drive cannot replace the remote SQL folder.

Each UNC role (final, secondary, temporary source mapping) can have a separate user/password. Blank user selects the AbaSqlBackup service identity. Blank password keeps only the same job's saved folder/user; changing either requires replacement credentials. SQL authentication and SQL backup/VERIFYONLY never use these NAS passwords. Network credentials use isolated outbound Windows logons and async impersonation; no global drive mappings are installed. Source handles are opened under source credentials before switching to destination credentials.

Passwords travel only in authenticated local requests, are DPAPI CurrentUser protected under the service identity, and are stored in schema 8 JobStorageSecrets/RunStorageSecrets. They never appear in normal job/run JSON, logs, exports, notifications or management responses. Saving active secrets and job metadata is transactional; enqueue stores independent protected run snapshots in the same transaction as the run. Missing/mismatched snapshots fail closed, without falling back to the service identity. Changing service identity requires re-entering passwords; older historical credentials may then be unavailable, so cleanup preserves those artifacts.

## Execution and failure behavior

The following transfer/cleanup sequence applies when the automatic check retains staging. Direct runs write to the final folder, record header/VERIFYONLY outcomes and only transfer the optional secondary copy.

1. SQL creates its backup in Temporary folder and records header/VERIFYONLY outcomes.
2. AbaSqlBackup reads the configured source and hashes it.
3. Each required final destination receives exclusive `.partial` output; SHA-256 must match before no-overwrite promotion. A failed destination does not discard another destination's successful copy.
4. Final copy evidence commits to SQLite before a separate temporary-cleanup intent.
5. The service rechecks final-copy hashes and removes the exact exclusively opened, hashed temporary file. Cleanup outcome appears in the job log; cleanup failure makes the overall job fail visibly without losing completed-copy evidence.

Failed backup/verification, failed required copy or cancellation preserves staging. An interrupted cleanup intent becomes Unknown on restart. No SQL operation or deletion is blindly replayed, and no age-based staging sweeper is added. Interrupted/unconfirmed files need operator inspection; a copy-only retry interface is not part of this refinement.

Final backups continue to use reviewed dependency-aware retention. Staging cleanup removes a redundant transport source only after final copies are durable; it is separate from deletion of recovery points. Primary final retention reads verified copy evidence rather than treating intentionally removed staging as missing backup evidence. Secondary windows remain independent. Legacy direct-write retention remains supported.

## Filenames and upgrade compatibility

New staging jobs use names such as `ExampleDatabase_Full_2026-10-20_18-14-25-1234567Z.bak`. Log files use `.trn`. Times are UTC, fractional seconds distinguish close executions, and `_02`, `_03`, etc. disambiguate database names that collide after sanitization/truncation/case folding. Run GUIDs remain internal identifiers. Any existing physical SQL/final path is rejected; it is never appended to or overwritten.

Schema 8 does not rewrite saved jobs or historical run snapshots. Null TemporaryFolder selects direct-write mode and preserves its established path/name format. Staging-enabled jobs may automatically use direct output per run when the checks above pass; historical run snapshots remain unchanged. To convert an existing job, open Store backups, choose its temporary folder and optional source mapping, then Save & Close and Save Job. Existing final/secondary folders keep their meaning. This explicit conversion avoids silently enabling an unavailable drive on a remote server; previously created filenames/retention evidence remain valid.




## Automatic selection validation

`dotnet run --project tests/SqlBackup.Tests --no-build -- --automatic-storage` uses simulated SQL and isolated files to check local/remote policy, staging fallback, secondary copies, immutable saved jobs, cancellation, and no retry after a direct SQL backup failure. It does not prove SQL service write permissions or a successful real backup. Real SQL acceptance remains separate.
