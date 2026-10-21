using System.Data;
using System.Globalization;
using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public sealed class SqlBackupHistoryInspector : IBackupHistoryInspector
{
    public async Task<bool> HasExternalDependenciesAsync(ConnectionSnapshot snapshot, string database,
        DateTimeOffset sinceUtc, string[] ownedBackupSetIds, CancellationToken cancellationToken)
    {
        await using var connection = SqlBackupEngine.CreateConnection(snapshot);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        // SQL backup history uses server-local wall times. Convert UTC on the server, with a safety margin.
        // A purged history cannot establish completeness; every owned set must still be represented.
        command.CommandText = """
            SELECT CONVERT(nvarchar(36), backup_set_uuid)
            FROM msdb.dbo.backupset
            WHERE database_name = @database AND type IN ('D', 'I', 'L')
              AND backup_start_date >= DATEADD(day, -1,
                  DATEADD(minute, DATEDIFF(minute, GETUTCDATE(), GETDATE()), @since));
            """;
        command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = database;
        command.Parameters.Add("@since", SqlDbType.DateTime2).Value = sinceUtc.UtcDateTime;
        var known = ownedBackupSetIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!known.Contains(id))
            {
                return true;
            }

            observed.Add(id);
        }

        return !known.IsSubsetOf(observed);
    }
}
