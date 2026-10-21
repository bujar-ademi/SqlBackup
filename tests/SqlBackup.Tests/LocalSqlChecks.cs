using Microsoft.Data.SqlClient;
using System.IO;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using System.Data;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class LocalSqlChecks
{
    public static async Task RunAsync(string instance)
    {
        if (!instance.StartsWith(@"(localdb)\AbaSqlBackupPhase2_", StringComparison.Ordinal))
        {
            throw new ArgumentException("SQL integration checks require a dedicated AbaSqlBackupPhase2_ LocalDB instance.");
        }

        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.SqlIntegration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string suffix = Guid.NewGuid().ToString("N");
        string database = "AbaSqlBackupTest_" + suffix;
        string restored = "AbaSqlBackupRestore_" + suffix;
        var snapshot = new ConnectionSnapshot(new ConnectionConfiguration(
            "Isolated LocalDB fixture", instance, true, null, TrustServerCertificate: true), null);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = instance,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            Pooling = false,
            ConnectTimeout = 15
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await ExecuteAsync(connection, $"""
                CREATE DATABASE {BackupSql.QuoteIdentifier(database)}
                ON (NAME = N'FixtureData', FILENAME = {Literal(Path.Combine(directory, "fixture.mdf"))})
                LOG ON (NAME = N'FixtureLog', FILENAME = {Literal(Path.Combine(directory, "fixture.ldf"))});
                """);
            await ExecuteAsync(connection, $"""
                CREATE TABLE {BackupSql.QuoteIdentifier(database)}.dbo.Probe (Id int NOT NULL);
                INSERT INTO {BackupSql.QuoteIdentifier(database)}.dbo.Probe VALUES (42);
                """);
            var engine = new SqlBackupEngine();
            var inspection = await engine.InspectAsync(snapshot, CancellationToken.None);
            Check(inspection.Databases.Any(item => item.Name == database && item.CanBackUp),
                "Real SQL capability inspection finds the isolated test database");

            var diagnostics = await new DestinationDiagnostics().CheckAsync(snapshot, directory, CancellationToken.None);
            Check(diagnostics.Contains("SQL Server sees folder: True", StringComparison.Ordinal),
                "Destination inspection runs under SQL Server's identity");
            var job = new BackupJob(Guid.NewGuid(), "Real SQL fixture", [database], directory);
            string path = BackupSql.CreateArtifactPath(job, database, Guid.NewGuid(), DateTimeOffset.UtcNow);
            var stages = new List<DatabaseBackupResult>();
            var result = await engine.ExecuteAsync(snapshot, job, database, path,
                progress =>
                {
                    stages.Add(progress);
                    return Task.CompletedTask;
                }, CancellationToken.None);
            Check(result.Backup == StageStatus.Succeeded && result.Verification == StageStatus.Succeeded,
                "Real Full backup with CHECKSUM and RESTORE VERIFYONLY succeeds");
            Check(result.SizeBytes > 0 && result.BackupSetId is not null && File.Exists(path),
                "Real artifact size, backup-set identity, and file are available");
            Check(stages.Any(stage => stage.Backup == StageStatus.Succeeded && stage.Verification == StageStatus.Pending),
                "Primary backup completion is reported before verification");

            await using (var restore = connection.CreateCommand())
            {
                restore.CommandTimeout = 120;
                restore.CommandText = $"""
                    RESTORE DATABASE {BackupSql.QuoteIdentifier(restored)} FROM DISK = @path
                    WITH MOVE N'FixtureData' TO @data, MOVE N'FixtureLog' TO @log, RECOVERY;
                    """;
                restore.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = path;
                restore.Parameters.Add("@data", SqlDbType.NVarChar, 1024).Value = Path.Combine(directory, "restored.mdf");
                restore.Parameters.Add("@log", SqlDbType.NVarChar, 1024).Value = Path.Combine(directory, "restored.ldf");
                await restore.ExecuteNonQueryAsync();
            }

            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = $"SELECT Id FROM {BackupSql.QuoteIdentifier(restored)}.dbo.Probe;";
                Check(Convert.ToInt32(await probe.ExecuteScalarAsync()) == 42,
                    "Manual restore into a separate test database preserves the seeded data");
            }

            await CheckStoredJobAsync(snapshot, job, directory);
            await LocalRecoveryChecks.RunAsync(connection, snapshot, database, directory);
            await LocalRetentionChecks.RunAsync(connection, snapshot, directory);
            await LocalOperationsChecks.RunAsync(snapshot, database, directory);
            await LocalStagingChecks.RunAsync(connection, snapshot, database, directory);

            var missing = job with { Destination = Path.Combine(directory, "does-not-exist") };
            string missingPath = BackupSql.CreateArtifactPath(missing, database, Guid.NewGuid(), DateTimeOffset.UtcNow);
            try
            {
                var missingResult = await engine.ExecuteAsync(snapshot, missing, database, missingPath,
                    _ => Task.CompletedTask, CancellationToken.None);
                Check(missingResult.Backup == StageStatus.Failed, "Missing destination fails without reporting success");
            }
            catch (ArgumentException)
            {
                Check(true, "Missing destination is rejected before writing a backup");
            }

            Console.WriteLine("SQL fixture artifacts retained at: " + directory);
        }
        finally
        {
            // Only the unique databases created by this fixture are removed; backup files are retained.
            foreach (string name in new[] { restored, database })
            {
                await ExecuteAsync(connection, $"""
                    IF DB_ID({Literal(name)}) IS NOT NULL
                    BEGIN
                        ALTER DATABASE {BackupSql.QuoteIdentifier(name)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE {BackupSql.QuoteIdentifier(name)};
                    END;
                    """);
            }
        }
    }

    private static string Literal(string value)
    {
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static async Task CheckStoredJobAsync(ConnectionSnapshot snapshot, BackupJob job, string directory)
    {
        var configuration = new ConfigurationStore(Path.Combine(directory, "application-state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        await jobs.SaveAsync(job with { Connection = snapshot.Configuration }, CancellationToken.None);
        using var worker = new BackupCoordinator(configuration, jobs, runs,
            new ConnectionSecrets(configuration), new SqlBackupEngine(), NullLogger<BackupCoordinator>.Instance);
        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            Guid runId = await worker.EnqueueAsync(job.Id, timeout.Token);
            while (true)
            {
                var run = (await runs.ListAsync(timeout.Token)).Single(item => item.Id == runId);
                if (run.Status is not (RunStatus.Queued or RunStatus.Running))
                {
                    Check(run.Status == RunStatus.Succeeded && run.Connection.Server == snapshot.Configuration.Server
                        && run.Job.Connection?.Server == snapshot.Configuration.Server
                        && await configuration.ReadAsync(timeout.Token) is null,
                        "Stored job executes a real SQL backup with its own connection and no shared connection record");
                    break;
                }

                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
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
