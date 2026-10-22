# Multiple backup destinations

Each job can have one to sixteen destinations: Local/Network/NAS folders, Azure Blob Storage containers, and Amazon S3 buckets. Configure them under Store backups, apply each destination, then Save & Close and Save Job. A job creates one SQL backup per database, verifies it when configured, and delivers that same artifact to every destination.

## SQL output and temporary files

The service selects a suitable local folder as direct SQL output when SQL is confirmed on the service computer and can see the folder on a fixed local drive. A source mapping or explicit folder credentials prevents this optimization. Different drive letters do not require staging. SQL directory visibility does not prove SQL write permissions; a failed SQL backup is never automatically rerun into another folder.

If no suitable local destination exists, SQL uses the job's temporary folder. Remote SQL requires the existing explicit service-visible source mapping and source-share credentials. Cloud-only jobs still need this readable source artifact. Temporary cleanup runs only after successful backup, requested VERIFYONLY, and all destination receipts have been persisted and reconfirmed. Failed transfers or cancellation preserve the source. Direct output is a retained destination and is never treated as disposable staging.

## Authentication

- Folder: an existing local directory or UNC share. Blank user uses the service identity; a UNC user/password is isolated to that destination.
- Azure Blob: an existing public Azure HTTPS container URL, for example `https://account.blob.core.windows.net/backups`, a prefix, and a separately entered SAS token. Do not include SAS queries in the container URL. SAS needs read and create/write access; reviewed deletion additionally needs delete access and appropriate version permissions when versioning is used. Sovereign clouds, managed identity and custom endpoints are not included in this implementation.
- Amazon S3: an existing standard bucket, AWS region, prefix, access key ID and secret access key. Temporary credentials also accept a session token. Permissions must cover GetObject/GetObjectVersion, PutObject and multipart operations, including AbortMultipartUpload; detecting missing keys through HEAD may require ListBucket restricted to the prefix. Reviewed cleanup additionally needs DeleteObject/DeleteObjectVersion. Dotted and special-purpose buckets, custom S3-compatible endpoints and ambient credential discovery are not supported.

Secrets belong to each job and destination. DPAPI CurrentUser protects secrets under the service identity. Ordinary job/run JSON, logs and management responses contain no SAS tokens, passwords, secret access keys or session tokens. Blank secret fields keep the same destination's saved credentials only when its provider/account/location identity still matches. Changing that identity requires entering credentials again. A changed session token requires re-entering the secret access key. Expired SAS/session credentials must be renewed before future runs.

## Transfers, retries and integrity

Cloud keys include the configured prefix, job ID, run ID and artifact filename. Folder copies use the unique artifact filename. Existing different content is never overwritten. Azure uploads use conditional block uploads and SDK transfer checksums. S3 uses bounded multipart streams, SDK checksum validation and conditional completion. Both confirm the resulting size, SHA-256 ownership metadata and recorded object identity/version. Failed S3 multipart operations attempt abort; an unconfirmed abort is reported for bucket inspection. A bucket lifecycle rule for aborting incomplete multipart uploads can provide additional operational cleanup without deleting completed recovery points.

An existing object encountered during retry is read and SHA-256 checked before it is accepted. Folder copies use exclusive partial files, SHA-256 checks and no-overwrite promotion. Destination receipts distinguish status, attempt count, size, SHA-256, object path and cloud ETag/version. Provider exceptions are converted to redacted actionable messages.

Transient transfers have at most three application attempts; SDKs also have bounded transport retries. Other destinations continue after an individual failure. A run is successful only when all required destinations and requested verification succeed. Retry destinations in the job log queues destination work from the retained source and original credential snapshots. It skips completed destinations and never repeats SQL backup or SQL verification. A changed or missing source fails safely. Service restart marks unfinished destination work Unknown rather than claiming completion or automatically replaying it.

## Reviewed retention

Every destination has an independent recovery window; zero keeps everything. Preview cleanup evaluates recorded ownership, verification, Full/Differential/Log dependencies and external SQL history. Missing or changed content, inaccessible credentials and uncertain dependencies block deletion. Cloud preview reads and hashes recorded objects; this can incur download charges and take longer for large backups. Cleanup rechecks content and deletes the recorded object/version conditionally using its ETag, with durable intent and outcome audit. Object-lock policies may reject deletion; this is recorded as failure, never success. No provider age-only expiration rule is installed by the application.

## Upgrade and evidence

Schema 9 adds separate job/run destination-secret tables. Existing job and run payloads are not rewritten. Legacy primary/secondary folder jobs keep their execution and retention behavior until edited through the destination list. Conversion can retain only that job's matching folder/user credentials. Historical retention uses each run's own original configuration and protected secrets.

Local validation includes simulated Azure/S3 destinations, real isolated folder copies, credential isolation, cloud-only staging, failed-destination retry without repeating SQL, and reviewed retention guards. SDK builds and simulated tests are not evidence of successful live cloud uploads. Real Azure/S3 credentials, large multipart uploads, provider policies, expired credentials and restore/download acceptance require separate environment validation. No cloud account, bucket/container or lifecycle policy is created automatically. Restore/download UI remains outside this feature.

Provider references: [Azure upload behavior](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-upload), [S3 conditional multipart completion](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TCompleteMultipartUploadRequest.html), [Azure immutability](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview), [S3 Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html).
