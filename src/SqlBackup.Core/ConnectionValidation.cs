namespace SqlBackup.Core;

public static class ConnectionValidation
{
    public static void Validate(ConnectionConfiguration? connection)
    {
        if (connection is null || string.IsNullOrWhiteSpace(connection.Name) || connection.Name.Length > 100
            || string.IsNullOrWhiteSpace(connection.Server) || connection.Server.Length > 256)
        {
            throw new ArgumentException("Configure this job's SQL Server connection before continuing.");
        }

        if (!connection.UseWindowsAuthentication
            && (string.IsNullOrWhiteSpace(connection.UserName) || connection.UserName.Length > 128))
        {
            throw new ArgumentException("SQL authentication requires a user name.");
        }
    }
}
