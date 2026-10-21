namespace SqlBackup.Core;

public sealed record ConnectionConfiguration(
    string Name,
    string Server,
    bool UseWindowsAuthentication,
    string? UserName,
    bool HasPassword = false,
    bool TrustServerCertificate = false);
