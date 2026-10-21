namespace SqlBackup.Core;

public sealed record ServerInspection(
    string Version,
    string Edition,
    bool SupportsCompression,
    string ServiceIdentity,
    DatabaseInfo[] Databases);
