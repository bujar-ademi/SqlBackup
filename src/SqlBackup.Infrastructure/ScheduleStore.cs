using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Globalization;

namespace SqlBackup.Infrastructure;

public sealed class ScheduleStore(ConfigurationStore store)
{
    public async Task<ScheduleStatus?> ReadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT JobRevision, CheckedUtc, Message FROM ScheduleState WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ScheduleStatus(jobId, reader.GetInt32(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2));
    }

    public async Task WriteAsync(ScheduleStatus status, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScheduleState (JobId, JobRevision, CheckedUtc, Message) VALUES ($id, $revision, $checked, $message)
            ON CONFLICT(JobId) DO UPDATE SET JobRevision = excluded.JobRevision, CheckedUtc = excluded.CheckedUtc, Message = excluded.Message;
            """;
        command.Parameters.AddWithValue("$id", status.JobId.ToString());
        command.Parameters.AddWithValue("$revision", status.JobRevision);
        command.Parameters.AddWithValue("$checked", status.CheckedUtc.ToString("O"));
        command.Parameters.AddWithValue("$message", status.Message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
