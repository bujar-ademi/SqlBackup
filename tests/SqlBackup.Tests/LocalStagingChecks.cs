using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class LocalStagingChecks
{
    public static async Task RunAsync(SqlConnection connection, ConnectionSnapshot snapshot, string database, string directory)
    {
        string staging = Directory.CreateDirectory(Path.Combine(directory, "sql-staging")).FullName;
        string final = Directory.CreateDirectory(Path.Combine(directory, "final-storage")).FullName;
        string secondary = Directory.CreateDirectory(Path.Combine(directory, "secondary-storage")).FullName;
        var configuration = new ConfigurationStore(Path.Combine(directory, "staging-state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var history = new HistoryStore(configuration);
        var job = new BackupJob(Guid.NewGuid(), "Real SQL staging fixture", [database], final,
            Connection: snapshot.Configuration, Storage: new StoragePolicy(SecondaryFolder: secondary), TemporaryFolder: staging);
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        using var worker = new BackupCoordinator(configuration, jobs, runs, new ConnectionSecrets(configuration),
            new SqlBackupEngine(), NullLogger<BackupCoordinator>.Instance);
        await worker.StartAsync(CancellationToken.None);
        string restored = "AbaSqlBackupStagingRestore_" + Guid.NewGuid().ToString("N");
        try
        {
            Guid id = await worker.EnqueueAsync(job.Id, CancellationToken.None);
            var completed = await WaitAsync(history, id);
            var result = completed.Databases.Single();
            Check(completed.Status == RunStatus.Succeeded && result.Verification == StageStatus.Succeeded
                && result.StagingCleanup == StageStatus.Succeeded && !File.Exists(result.ArtifactPath)
                && result.Copies?.Length == 2 && result.Copies.All(copy => File.Exists(copy.Path) && copy.Status == StageStatus.Succeeded),
                "Real SQL backup/VERIFYONLY uses temporary storage, persists both checked copies and removes the temporary file");
            await using (var restore = connection.CreateCommand())
            {
                restore.CommandTimeout = 120;
                restore.CommandText = $"""
                    RESTORE DATABASE {BackupSql.QuoteIdentifier(restored)} FROM DISK = @path
                    WITH MOVE N'FixtureData' TO @data, MOVE N'FixtureLog' TO @log, RECOVERY;
                    """;
                restore.Parameters.AddWithValue("@path", result.Copies![0].Path);
                restore.Parameters.AddWithValue("@data", Path.Combine(directory, "staging-restored.mdf"));
                restore.Parameters.AddWithValue("@log", Path.Combine(directory, "staging-restored.ldf"));
                await restore.ExecuteNonQueryAsync();
            }

            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = $"SELECT Id FROM {BackupSql.QuoteIdentifier(restored)}.dbo.Probe;";
                Check(Convert.ToInt32(await probe.ExecuteScalarAsync()) == 42,
                    "A copied final backup restores the seeded SQL data after its temporary source is deleted");
            }

            var missingFinal = job with { Id = Guid.NewGuid(), Revision = 0, Destination = Path.Combine(directory, "missing-final") };
            await jobs.SaveAsync(missingFinal, CancellationToken.None);
            var failed = await WaitAsync(history, await worker.EnqueueAsync(missingFinal.Id, CancellationToken.None));
            var preserved = failed.Databases.Single();
            Check(failed.Status == RunStatus.Failed && preserved.Backup == StageStatus.Succeeded
                && preserved.Verification == StageStatus.Succeeded && File.Exists(preserved.ArtifactPath)
                && preserved.Copies![0].Status == StageStatus.Failed && preserved.Copies[1].Status == StageStatus.Succeeded,
                "A failed final destination preserves the real SQL temporary backup and the independent secondary copy");
            var rejected = await new SqlBackupEngine().ExecuteAsync(snapshot, missingFinal, database, preserved.ArtifactPath!,
                _ => Task.CompletedTask, CancellationToken.None);
            Check(rejected.Backup == StageStatus.Failed && File.Exists(preserved.ArtifactPath),
                "SQL rejects an existing readable artifact name without appending to or overwriting its backup");

            var missingTemporary = job with
            {
                Id = Guid.NewGuid(),
                Revision = 0,
                TemporaryFolder = Path.Combine(directory, "missing-sql-staging")
            };
            await jobs.SaveAsync(missingTemporary, CancellationToken.None);
            var unavailable = await WaitAsync(history, await worker.EnqueueAsync(missingTemporary.Id, CancellationToken.None));
            Check(unavailable.Status == RunStatus.Failed && unavailable.Databases.Single().Backup == StageStatus.Failed,
                "A missing SQL temporary folder fails clearly without silently using the final destination");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await using var drop = connection.CreateCommand();
            drop.CommandTimeout = 120;
            string literal = "N'" + restored.Replace("'", "''", StringComparison.Ordinal) + "'";
            drop.CommandText = $"""
                IF DB_ID({literal}) IS NOT NULL
                BEGIN
                    ALTER DATABASE {BackupSql.QuoteIdentifier(restored)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {BackupSql.QuoteIdentifier(restored)};
                END;
                """;
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<BackupRun> WaitAsync(HistoryStore history, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var run = await history.GetAsync(id, timeout.Token);
            if (run.Status is not (RunStatus.Queued or RunStatus.Running))
            {
                return run;
            }

            await Task.Delay(25, timeout.Token);
        }
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
