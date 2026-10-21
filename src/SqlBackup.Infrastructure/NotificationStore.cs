using Microsoft.Data.Sqlite;
using SqlBackup.Core;
using System.Text.Json;

namespace SqlBackup.Infrastructure;

public sealed class NotificationStore(ConfigurationStore store)
{
    internal static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        NotificationMessage notification, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO Notifications (EventKey, CreatedUtc, Payload) VALUES ($key, $created, $payload);";
        command.Parameters.AddWithValue("$key", notification.EventKey);
        command.Parameters.AddWithValue("$created", notification.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(notification));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<NotificationPage> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Notifications WHERE ReadUtc IS NULL;";
        int unread = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        command.CommandText = "SELECT Payload, ReadUtc FROM Notifications ORDER BY (ReadUtc IS NULL) DESC, CreatedUtc DESC, rowid DESC LIMIT 100;";
        var items = new List<NotificationMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = JsonSerializer.Deserialize<NotificationMessage>(reader.GetString(0))
                    ?? throw new InvalidDataException("Invalid notification history.");
                items.Add(item with { ReadUtc = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)) });
            }
        }

        transaction.Commit();
        return new NotificationPage(items.ToArray(), unread);
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Notifications SET ReadUtc = $read WHERE ReadUtc IS NULL;";
        command.Parameters.AddWithValue("$read", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkReadAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            throw new ArgumentException("Select a valid notification.");
        }

        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Notifications SET ReadUtc = COALESCE(ReadUtc, $read) WHERE EventKey = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$read", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
