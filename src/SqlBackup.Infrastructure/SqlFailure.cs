using Microsoft.Data.SqlClient;
using System.Security.Cryptography;

namespace SqlBackup.Infrastructure;

public static class SqlFailure
{
    public static string Describe(Exception exception)
    {
        if (exception is SqlException sql)
        {
            foreach (SqlError error in sql.Errors)
            {
                switch (error.Number)
                {
                    case 18456:
                        return "SQL login failed. Check the saved credentials and service identity.";
                    case 229:
                    case 262:
                    case 297:
                    case 300:
                        return "SQL permission denied. Check backup, inspection, and VERIFYONLY permissions for this login.";
                    case 3201:
                    case 3271:
                    case 112:
                        return "SQL Server could not write/read the backup device. Check its service account, folder/share access, and free disk space.";
                    case 911:
                    case 916:
                    case 942:
                    case 945:
                        return "The database is unavailable or inaccessible to the configured SQL login.";
                    case -2:
                        return "SQL request timed out. Inspect SQL Server before retrying an operation with an uncertain outcome.";
                    case 53:
                    case 40:
                    case 10060:
                        return "SQL Server could not be reached. Check the instance name, network, and SQL listener.";
                }
            }

            return $"SQL operation failed (error {sql.Number}). Inspect SQL Server diagnostics; no raw server message is returned.";
        }

        if (exception is CryptographicException)
        {
            return "The SQL password cannot be decrypted by this service identity. Re-enter and save it.";
        }

        if (exception is ArgumentException)
        {
            return exception.Message;
        }

        return "Operation failed. Check SQL connectivity, service permissions, and local history storage.";
    }
}
