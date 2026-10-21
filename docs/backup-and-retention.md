# Backup and retention rules

Retention cleanup requires explicit preview and confirmation; unattended deletion remains deferred.

## Backup prerequisites

Inspect database state, recovery model, server edition/version, backup permissions, and supported options before execution. A Log backup requires an eligible recovery model and an established log backup chain. Report unmet prerequisites without changing the database automatically.

Use unique files per database and execution. Avoid overwriting existing artifacts. Quote database identifiers safely and validate path/configuration inputs; do not build unrestricted SQL from user-entered text.

Normal scheduled Full backups must support the intended Differential strategy. An optional ad hoc copy-only Full backup must be explicitly distinguished from a Differential base.

## Illustrative schedule

Full once a week, Differential on the other days, and Log every 15 minutes is a discussion example, not a universal default or an already configured production policy. Recovery objectives, database size, recovery model, and storage limits determine the actual schedule.

## Stage outcomes

Persist the primary backup result before starting dependent work. A verification or copy failure records a failed stage and a visible overall issue, while preserving the fact that the primary artifact was created.

Checksums and RESTORE VERIFYONLY provide specific checks; neither establishes that a complete restore succeeds. Only a separately executed and validated restore test can be labeled as a tested restore.

For staging-enabled jobs, SQL writes/VERIFYONLY uses the configured Temporary folder. Final-copy completion and hashes commit before temporary deletion intent. Recheck final hashes and delete only the exact hashed source; failed required copies, verification or cancellation preserve staging. Temporary removal does not delete a recovery point because verified final copies remain; it has its own observed outcome. Reviewed retention targets those final copies, using copy evidence and independent location credentials. Legacy direct-write jobs retain their original artifact ownership checks. See staging-and-nas.md.

## Backup metadata

Record database identity, backup type, backup set identity, source server, times, paths, size, checksum/verification results, copy results, and available LSN/base/fork metadata needed to reason about dependencies.

Account for backups performed by other tools. External Full backups may affect Differential bases, and external Log backups may be needed for recovery. Never infer a complete recoverable chain solely from files created by this application.

## Retention invariants

- Delete only application-owned artifacts in explicitly configured managed locations.
- Never delete files based on filename or age alone.
- Retain the Full base required by any retained Differential backup.
- Preserve the uninterrupted Log sequence and required base artifacts for the advertised recovery window.
- Treat primary and secondary destinations separately when assessing what each can recover.
- Preserve artifacts when dependency metadata is missing or uncertain, and report the reason cleanup was skipped.
- Do not remove an artifact being written, verified, or copied.
- Record each deletion decision and its outcome.

The retention setting describes a desired recovery window. Dependencies can require older files to remain, so storage usage may exceed the nominal age setting. Show this explicitly.

Use the inspectable cleanup preview before confirming deletion. Cleanup must remain conservative when external backups or missing files make the chain incomplete.

## Failure handling

Handle permission errors, storage exhaustion, SQL/network interruption, service restart, and partial copies explicitly. A secondary copy becomes complete only after transfer completion and the selected integrity check. Interrupted output must not appear as a usable completed backup.

An inaccessible destination is a failure condition, not evidence that its files have been removed. Do not erase historical metadata simply because a share is temporarily offline.

