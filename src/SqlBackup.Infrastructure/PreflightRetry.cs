using Microsoft.Data.SqlClient;

namespace SqlBackup.Infrastructure;

public static class PreflightRetry
{
    public static bool IsTransient(Exception exception)
    {
        return exception is TimeoutException
            || exception is SqlException { Number: -2 or 53 or 64 or 233 or 10053 or 10054 or 10060 };
    }
}
