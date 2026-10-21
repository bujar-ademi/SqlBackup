# Per-job SQL staging and final storage

SQL temporary staging and final storage use independent paths and Windows identities.

## Configuration and identities

New jobs use `%ProgramData%\SqlBackup\Temp` as the initial Temporary folder (resolved to an absolute path on the application host). The folder remains editable. Save & Close checks its visibility through SQL Server and blocks applying the draft with a warning if it is missing or inaccessible. For remote SQL, choose a folder on that SQL host and configure the service-visible source mapping. Existing jobs retain their configured paths. The application does not create directories remotely. SQL Server's service account must be able to write/read this folder. Visibility diagnostics cannot prove SQL write permission; the actual backup performs that write.

Store backups specifies the final folder visible to AbaSqlBackup, with one optional secondary folder. For a remote SQL host, Service-visible temporary folder must expose the same SQL-written files through an authorized UNC share, or run AbaSqlBackup on that SQL host. A local desktop drive cannot replace the remote SQL folder.

Each UNC role (final, secondary, temporary source mapping) can have a separate user/password. Blank user selects the AbaSqlBackup service identity. Blank password keeps only the same job's saved folder/user; changing either requires replacement credentials. SQL authentication and SQL backup/VERIFYONLY never use these NAS passwords. Network credentials use isolated outbound Windows logons and async impersonation; no global drive mappings are installed. Source handles are opened under source credentials before switching to destination credentials.

Passwords travel only in authenticated local requests, are DPAPI CurrentUser protected under the service identity, and are stored in schema 8 JobStorageSecrets/RunStorageSecrets. They never appear in normal job/run JSON, logs, exports, notifications or management responses. Saving active secrets and job metadata is transactional; enqueue stores independent protected run snapshots in the same transaction as the run. Missing/mismatched snapshots fail closed, without falling back to the service identity. Changing service identity requires re-entering passwords; older historical credentials may then be unavailable, so cleanup preserves those artifacts.

## Execution and failure behavior

1. SQL creates its backup in Temporary folder and records header/VERIFYONLY outcomes.
2. AbaSqlBackup reads the configured source and hashes it.
3. Each required final destination receives exclusive `.partial` output; SHA-256 must match before no-overwrite promotion. A failed destination does not discard another destination's successful copy.
4. Final copy evidence commits to SQLite before a separate temporary-cleanup intent.
5. The service rechecks final-copy hashes and removes the exact exclusively opened, hashed temporary file. Cleanup outcome appears in the job log; cleanup failure makes the overall job fail visibly without losing completed-copy evidence.

Failed backup/verification, failed required copy or cancellation preserves staging. An interrupted cleanup intent becomes Unknown on restart. No SQL operation or deletion is blindly replayed, and no age-based staging sweeper is added. Interrupted/unconfirmed files need operator inspection; a copy-only retry interface is not part of this refinement.

Final backups continue to use reviewed dependency-aware retention. Staging cleanup removes a redundant transport source only after final copies are durable; it is separate from deletion of recovery points. Primary final retention reads verified copy evidence rather than treating intentionally removed staging as missing backup evidence. Secondary windows remain independent. Legacy direct-write retention remains supported.

## Filenames and upgrade compatibility

New staging jobs use names such as `ExampleDatabase_Full_2026-10-20_18-14-25-1234567Z.bak`. Log files use `.trn`. Times are UTC, fractional seconds distinguish close executions, and `_02`, `_03`, etc. disambiguate database names that collide after sanitization/truncation/case folding. Run GUIDs remain internal identifiers. Any existing physical SQL/final path is rejected; it is never appended to or overwritten.

Schema 8 does not rewrite saved jobs or historical run snapshots. Null TemporaryFolder identifies the old direct-write path/name format. To convert an existing job, open Store backups, choose its temporary folder and optional source mapping, then Save & Close and Save Job. Existing final/secondary folders keep their meaning. This explicit conversion avoids silently enabling an unavailable drive on a remote server; previously created filenames/retention evidence remain valid.



