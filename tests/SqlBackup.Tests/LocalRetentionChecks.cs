using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class LocalRetentionChecks
{
    public static async Task RunAsync(SqlConnection connection, ConnectionSnapshot snapshot, string directory)
    {
        string database = "AbaSqlBackupRetention_" + Guid.NewGuid().ToString("N");
        string restored = "AbaSqlBackupRetained_" + Guid.NewGuid().ToString("N");
        string primary = Path.Combine(directory, "retention-primary");
        string secondary = Path.Combine(directory, "retention-secondary");
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(secondary);
        var configuration = new ConfigurationStore(Path.Combine(directory, "retention-state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var audits = new CleanupAuditStore(configuration);
        var job = new BackupJob(Guid.NewGuid(), "Real retention fixture", [database], primary, CopyOnly: false,
            Connection: snapshot.Configuration, Storage: new StoragePolicy(7, secondary, 7));
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        try
        {
            await ExecuteAsync(connection, $"""
                CREATE DATABASE {BackupSql.QuoteIdentifier(database)}
                ON PRIMARY (NAME = N'RetentionData', FILENAME = N'{Path.Combine(directory, "retention.mdf").Replace("'", "''")}')
                LOG ON (NAME = N'RetentionLog', FILENAME = N'{Path.Combine(directory, "retention.ldf").Replace("'", "''")}');
                ALTER DATABASE {BackupSql.QuoteIdentifier(database)} SET RECOVERY FULL;
                ALTER DATABASE {BackupSql.QuoteIdentifier(database)} SET AUTO_CLOSE ON;
                """);
            await ExecuteAsync(connection, $"""
                CREATE TABLE {BackupSql.QuoteIdentifier(database)}.dbo.Probe (Id int NOT NULL PRIMARY KEY);
                INSERT INTO {BackupSql.QuoteIdentifier(database)}.dbo.Probe VALUES (42);
                """);
            var old = await BackupAsync(runs, snapshot, job, BackupType.Full, -30);
            var full = await BackupAsync(runs, snapshot, job, BackupType.Full, -10);
            await ExecuteAsync(connection, $"INSERT INTO {BackupSql.QuoteIdentifier(database)}.dbo.Probe VALUES (43);");
            var diff = await BackupAsync(runs, snapshot, job, BackupType.Differential, -3);
            await ExecuteAsync(connection, $"INSERT INTO {BackupSql.QuoteIdentifier(database)}.dbo.Probe VALUES (44);");
            var log = await BackupAsync(runs, snapshot, job, BackupType.Log, -2);
            var retention = new RetentionService(jobs, runs, audits, new ConnectionSecrets(configuration), new SqlBackupHistoryInspector());
            var preview = await retention.PreviewAsync(job.Id, CancellationToken.None);
            Check(preview.Decisions.Count(item => item.Delete) == 2,
                "Real SQL history and headers identify only the superseded Full in each managed location");
            var cleanup = await retention.ExecuteAsync(job.Id, preview.Token, CancellationToken.None);
            Check(cleanup.Count(item => item.Outcome == "Deleted") == 2
                && !File.Exists(old.Databases[0].ArtifactPath)
                && !File.Exists(old.Databases[0].Copies![0].Path),
                "Isolated cleanup removes exactly the reviewed older primary and secondary Full files");
            await using var restore = connection.CreateCommand();
            restore.CommandTimeout = 120;
            restore.CommandText = $"""
                RESTORE DATABASE {BackupSql.QuoteIdentifier(restored)} FROM DISK = @full
                WITH MOVE N'RetentionData' TO @data, MOVE N'RetentionLog' TO @logfile, NORECOVERY;
                RESTORE DATABASE {BackupSql.QuoteIdentifier(restored)} FROM DISK = @diff WITH NORECOVERY;
                RESTORE LOG {BackupSql.QuoteIdentifier(restored)} FROM DISK = @log WITH RECOVERY;
                """;
            restore.Parameters.Add("@full", SqlDbType.NVarChar, 1024).Value = full.Databases[0].Copies![0].Path;
            restore.Parameters.Add("@diff", SqlDbType.NVarChar, 1024).Value = diff.Databases[0].Copies![0].Path;
            restore.Parameters.Add("@log", SqlDbType.NVarChar, 1024).Value = log.Databases[0].Copies![0].Path;
            restore.Parameters.Add("@data", SqlDbType.NVarChar, 1024).Value = Path.Combine(directory, "retained.mdf");
            restore.Parameters.Add("@logfile", SqlDbType.NVarChar, 1024).Value = Path.Combine(directory, "retained.ldf");
            await restore.ExecuteNonQueryAsync();
            restore.Parameters.Clear();
            restore.CommandText = $"SELECT SUM(Id) FROM {BackupSql.QuoteIdentifier(restored)}.dbo.Probe;";
            Check(Convert.ToInt32(await restore.ExecuteScalarAsync()) == 129,
                "After cleanup, retained secondary Full + Differential + Log restore all expected data");
            Check((await retention.PreviewAsync(job.Id, CancellationToken.None)).Decisions.All(item => !item.Delete),
                "A second real preview respects audited deletions and keeps the remaining chain");

            string external = Path.Combine(directory, "external-fixture.bak");
            await using var externalBackup = connection.CreateCommand();
            externalBackup.CommandText = $"BACKUP DATABASE {BackupSql.QuoteIdentifier(database)} TO DISK = @path WITH COPY_ONLY, CHECKSUM;";
            externalBackup.Parameters.AddWithValue("@path", external);
            await externalBackup.ExecuteNonQueryAsync();
            Check((await retention.PreviewAsync(job.Id, CancellationToken.None)).Decisions.All(item => !item.Delete),
                "An untracked real SQL backup conservatively preserves every remaining artifact");
        }
        finally
        {
            foreach (string name in new[] { restored, database })
            {
                await ExecuteAsync(connection, $"""
                    IF DB_ID(N'{name}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE {BackupSql.QuoteIdentifier(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE {BackupSql.QuoteIdentifier(name)};
                    END;
                    """);
            }
        }
    }

    private static async Task<BackupRun> BackupAsync(RunStore runs, ConnectionSnapshot snapshot,
        BackupJob job, BackupType type, int ageDays)
    {
        Guid id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow.AddDays(ageDays);
        string path = BackupSql.CreateArtifactPath(job, job.Databases[0], id, created, type);
        var result = await new SqlBackupEngine().ExecuteAsync(snapshot, job, job.Databases[0], path,
            _ => Task.CompletedTask, CancellationToken.None, type);
        result = await new ArtifactTransfer().ProcessAsync(job, result, CancellationToken.None);
        Check(result.Backup == StageStatus.Succeeded && result.Verification == StageStatus.Succeeded
            && result.Copies?.Single().Status == StageStatus.Succeeded,
            $"Real {type} backup is verified, fingerprinted and copied with integrity evidence: {result.Message}");
        var run = new BackupRun(id, job, snapshot.Configuration, created, RunStatus.Succeeded,
            [result], "Isolated aged-metadata fixture", Type: type);
        await runs.AddAsync(run, snapshot.ProtectedPassword, CancellationToken.None);
        return run;
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
