using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Tests;

public static class LocalRecoveryChecks
{
    public static async Task RunAsync(SqlConnection connection, ConnectionSnapshot snapshot, string database, string directory)
    {
        var engine = new SqlBackupEngine();
        var job = new BackupJob(Guid.NewGuid(), "Recovery-chain fixture", [database], directory, CopyOnly: false);
        var simpleLog = await BackupAsync(engine, snapshot, job, BackupType.Log);
        Check(simpleLog.Backup == StageStatus.Failed, "Log backup is rejected in SIMPLE recovery without changing the database");
        await ExecuteAsync(connection, $"ALTER DATABASE {BackupSql.QuoteIdentifier(database)} SET RECOVERY FULL;");
        var full = await BackupAsync(engine, snapshot, job, BackupType.Full);
        await ExecuteAsync(connection, $"INSERT INTO {BackupSql.QuoteIdentifier(database)}.dbo.Probe VALUES (43);");
        var differential = await BackupAsync(engine, snapshot, job, BackupType.Differential);
        await ExecuteAsync(connection, $"INSERT INTO {BackupSql.QuoteIdentifier(database)}.dbo.Probe VALUES (44);");
        var log1 = await BackupAsync(engine, snapshot, job, BackupType.Log);
        DateTime stopAt;
        await using (var clock = connection.CreateCommand())
        {
            clock.CommandText = "SELECT GETDATE();";
            stopAt = (DateTime)(await clock.ExecuteScalarAsync())!;
        }

        await Task.Delay(150);
        await ExecuteAsync(connection, $"INSERT INTO {BackupSql.QuoteIdentifier(database)}.dbo.Probe VALUES (45);");
        var log2 = await BackupAsync(engine, snapshot, job, BackupType.Log);
        Check(new[] { full, differential, log1, log2 }.All(result => result.Backup == StageStatus.Succeeded
            && result.Verification == StageStatus.Succeeded), "Real Full, Differential and two Log backups pass VERIFYONLY");
        Check(differential.DatabaseBackupLsn == full.CheckpointLsn, "Differential header points to the regular Full base");

        string target = "AbaSqlBackupChain_" + Guid.NewGuid().ToString("N");
        try
        {
            await using var restore = connection.CreateCommand();
            restore.CommandTimeout = 120;
            restore.CommandText = $"""
                RESTORE DATABASE {BackupSql.QuoteIdentifier(target)} FROM DISK = @full
                WITH MOVE N'FixtureData' TO @data, MOVE N'FixtureLog' TO @log, NORECOVERY;
                RESTORE DATABASE {BackupSql.QuoteIdentifier(target)} FROM DISK = @diff WITH NORECOVERY;
                RESTORE LOG {BackupSql.QuoteIdentifier(target)} FROM DISK = @log1 WITH NORECOVERY;
                RESTORE LOG {BackupSql.QuoteIdentifier(target)} FROM DISK = @log2 WITH STOPAT = @point, RECOVERY;
                """;
            restore.Parameters.Add("@full", SqlDbType.NVarChar, 1024).Value = full.ArtifactPath;
            restore.Parameters.Add("@diff", SqlDbType.NVarChar, 1024).Value = differential.ArtifactPath;
            restore.Parameters.Add("@log1", SqlDbType.NVarChar, 1024).Value = log1.ArtifactPath;
            restore.Parameters.Add("@log2", SqlDbType.NVarChar, 1024).Value = log2.ArtifactPath;
            restore.Parameters.Add("@data", SqlDbType.NVarChar, 1024).Value = Path.Combine(directory, "chain.mdf");
            restore.Parameters.Add("@log", SqlDbType.NVarChar, 1024).Value = Path.Combine(directory, "chain.ldf");
            restore.Parameters.Add("@point", SqlDbType.DateTime).Value = stopAt;
            await restore.ExecuteNonQueryAsync();
            restore.Parameters.Clear();
            restore.CommandText = $"SELECT SUM(Id) FROM {BackupSql.QuoteIdentifier(target)}.dbo.Probe;";
            Check(Convert.ToInt32(await restore.ExecuteScalarAsync()) == 129,
                "Full + Differential + Log restore reaches the requested point before the later transaction");
        }
        finally
        {
            await ExecuteAsync(connection, $"""
                IF DB_ID(N'{target}') IS NOT NULL
                BEGIN
                    ALTER DATABASE {BackupSql.QuoteIdentifier(target)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {BackupSql.QuoteIdentifier(target)};
                END;
                """);
        }
    }

    private static async Task<DatabaseBackupResult> BackupAsync(SqlBackupEngine engine, ConnectionSnapshot snapshot, BackupJob job, BackupType type)
    {
        string path = BackupSql.CreateArtifactPath(job, job.Databases[0], Guid.NewGuid(), DateTimeOffset.UtcNow, type);
        return await engine.ExecuteAsync(snapshot, job, job.Databases[0], path, _ => Task.CompletedTask, CancellationToken.None, type);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        Console.WriteLine("PASS: " + message);
    }
}
