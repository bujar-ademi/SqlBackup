# Job configuration

Create a job using the plus button. Each job owns its SQL connection, selected databases, folders, schedule and notification settings.

## Connection and databases

Open Connect to Microsoft SQL Server to set the server, authentication, encryption and certificate options. Test the connection. Select databases through the separate checkbox dialog; system databases can be shown explicitly. Loading supports Cancel and Refresh.

Save & Close applies dialog edits to the current draft. Save job persists the complete job. Cancel discards dialog edits. Blank passwords preserve only matching saved credentials for that same job and target.

## Storage

Open Store backups in selected destinations. Set the final Local/Network Folder/NAS path and optional secondary copy. Temporary folder defaults to `%ProgramData%\SqlBackup\Temp` and remains editable. It must be visible to SQL Server; Save & Close checks visibility and warns if it is missing or inaccessible. The dialog does not create folders or grant permissions. SQL write access is checked by the actual backup operation.

For remote SQL, use a temporary path on that server and a service-visible source mapping to the same files. Configure independent UNC credentials where needed. See [staging and NAS](staging-and-nas.md).

Retention is expressed in days for each final destination; 0 keeps everything. Open Cleanup & audit to preview and explicitly confirm eligible deletions. Dependencies may require older files to remain. Automatic retention deletion is not implemented.

## Schedule and execution

The Schedule gear opens interval, start-time, time-zone, weekday, monthly and missed-run settings with an estimated preview. Save the job to persist changes. See [scheduling rules](advanced-schedule-design.md).

Run Now queues a saved job after review. The service continues execution when the desktop closes. History shows per-stage results; double-click a row or use Open selected job log for details. Exported logs can contain private database names and paths.

## Notifications and diagnostics

Send confirmation controls local success/failure notifications. The inbox persists until acknowledged; no email is sent. More settings provides database health and diagnostics. About identifies the application author and links to online documentation.
