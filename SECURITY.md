# Security

Do not post passwords, connection strings, configuration databases, backup files or exploitable security details in public issues.

Use GitHub's private vulnerability reporting option in this repository's Security tab when available. If it is unavailable, open an issue requesting a private reporting channel without including vulnerability details. There is no guaranteed response time or paid support commitment. Security fixes target the latest published prerelease; no long-term maintenance branches are promised.

Desktop management requires an elevated local administrator. The service runs as LocalSystem and accepts authenticated local named-pipe requests; it exposes no network management endpoint. Treat access to the service host as privileged access.

SQL and storage credentials are protected with Windows DPAPI under the service identity. Moving configuration.db to another account or machine does not migrate those credentials. Protect the whole data directory and recovery snapshots. Exported job logs may contain database names and filesystem paths; review them before sharing.

Azure SAS tokens, S3 secret access keys and session tokens are stored separately from ordinary job/run metadata and are never returned to the desktop. Destinations use explicit credentials, scoped to their job, with protected execution snapshots. Use private containers/buckets and narrowly scoped provider permissions. Cloud upload confirmation is not a tested restore; validate provider access and recovery in your own environment before relying on the copies.

SQL Server uses its own service identity to write backups. NAS credentials used by AbaSqlBackup do not grant SQL Server access. Grant folder-specific permissions and do not expose the configuration database to SQL or network shares.

Installers are currently unsigned. Verify download provenance and the supplied SHA-256 checksum; a checksum alone does not authenticate a publisher. See [installation and recovery](docs/installation-and-recovery.md).
