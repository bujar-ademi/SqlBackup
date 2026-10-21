using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;
using System.Text.Json;

namespace SqlBackup.Tests;

public static class Phase2Checks
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.Tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationStore(directory);
        await configuration.InitializeAsync(CancellationToken.None);
        await configuration.SaveAsync(new ConnectionConfiguration("Fictional", "first-server", false, "first-user"),
            "FictionalOriginalSecret", CancellationToken.None);
        await using (var legacy = new SqliteConnection(configuration.ConnectionString))
        {
            await legacy.OpenAsync();
            await using var command = legacy.CreateCommand();
            command.CommandText = "DROP TABLE Jobs; DROP TABLE Runs; PRAGMA user_version = 1;";
            await command.ExecuteNonQueryAsync();
        }

        await configuration.InitializeAsync(CancellationToken.None);
        Check((await configuration.ReadAsync(CancellationToken.None))?.Server == "first-server",
            "Schema 1 upgrades to the jobs schema without changing the existing connection");
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var secrets = new ConnectionSecrets(configuration);
        await CheckLegacyJobMigrationAsync(configuration, jobs, secrets);
        var first = new BackupJob(Guid.NewGuid(), "First job", ["TestDatabase"], @"C:\FictionalBackups",
            Connection: new ConnectionConfiguration("First connection", "first-server", false, "first-user"));
        var second = first with
        {
            Id = Guid.NewGuid(),
            Name = "Second job",
            Destination = @"\\fictional-host\backup-share",
            Connection = new ConnectionConfiguration("Second connection", "second-server", false, "second-user")
        };
        await jobs.SaveAsync(first, CancellationToken.None, "FirstJobSecret");
        await jobs.SaveAsync(second, CancellationToken.None, "SecondJobSecret");
        first = await jobs.GetAsync(first.Id, CancellationToken.None);
        second = await jobs.GetAsync(second.Id, CancellationToken.None);
        Check((await new JobStore(configuration).ListAsync(CancellationToken.None)).Length == 2, "Independent jobs survive reopening");
        Check(ConnectionSecrets.UnprotectPassword(await secrets.ReadSnapshotAsync(first.Id, CancellationToken.None)) == "FirstJobSecret"
            && ConnectionSecrets.UnprotectPassword(await secrets.ReadSnapshotAsync(second.Id, CancellationToken.None)) == "SecondJobSecret",
            "Each job owns a separate SQL server and protected password");
        await RejectAsync(() => jobs.SaveAsync(first with { Id = Guid.NewGuid(), Revision = 0 }, CancellationToken.None),
            "A new job cannot borrow another job's saved password");
        await RejectAsync(() => jobs.SaveAsync(first with
        {
            Connection = first.Connection! with { Server = "different-target" }
        }, CancellationToken.None), "Changing one job's target requires that job's password");
        await jobs.SaveAsync(first with { Name = "Updated job" }, CancellationToken.None);
        await RejectAsync(() => jobs.SaveAsync(first, CancellationToken.None), "Stale job edits are rejected");
        first = await jobs.GetAsync(first.Id, CancellationToken.None);

        foreach (string invalidPath in new[] { "relative", @"C:\backups\..\other", @"\\?\C:\backups", @"C:\bad*folder" })
        {
            await RejectAsync(() =>
            {
                JobValidation.Validate(first with { Destination = invalidPath });
                return Task.CompletedTask;
            }, "Unsafe or relative destination rejected");
        }

        Guid artifactRunId = Guid.NewGuid();
        string path = BackupSql.CreateArtifactPath(first, "TestDatabase", artifactRunId, DateTimeOffset.UtcNow);
        string anotherPath = BackupSql.CreateArtifactPath(first, "TestDatabase", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Check(path != anotherPath && path.EndsWith(".bak"), "Runs produce distinct artifact names");
        using (var connection = new SqlConnection())
        {
            using var command = BackupSql.CreateBackupCommand(connection, first, "name]; DROP DATABASE [other", path);
            Check(command.CommandText.Contains("[name]]; DROP DATABASE [other]", StringComparison.Ordinal)
                && !command.CommandText.Contains(path, StringComparison.Ordinal)
                && command.CommandText.Contains("COPY_ONLY", StringComparison.Ordinal)
                && !command.CommandText.Contains("WITH INIT", StringComparison.Ordinal),
                "SQL identifiers are escaped, paths are parameters, and manual defaults preserve the Differential base");
        }

        var engine = new SimulatedSqlEngine { BlockExecution = true, FailVerification = true };
        using var coordinator = new BackupCoordinator(configuration, jobs, runs, secrets, engine, NullLogger<BackupCoordinator>.Instance);
        var handler = new ManagementHandler(jobs, runs, secrets, engine, new DestinationDiagnostics(),
            coordinator, NullLogger<ManagementHandler>.Instance);
        var draft = first with { Connection = first.Connection! with { Server = "unsaved-draft-server" } };
        var inspected = await handler.HandleAsync(new ServiceRequest("Inspect", Job: draft, Password: "UnsavedDraftSecret"), CancellationToken.None);
        Check(inspected.Success && engine.InspectedServer == "unsaved-draft-server"
            && engine.InspectedPassword == "UnsavedDraftSecret"
            && (await jobs.GetAsync(first.Id, CancellationToken.None)).Connection?.Server == "first-server",
            "Inspection uses the selected job's unsaved connection without changing persisted credentials");
        var legacySave = await handler.HandleAsync(new ServiceRequest("Save", Configuration: first.Connection,
            Password: "UnusedGlobalSecret"), CancellationToken.None);
        Check(!legacySave.Success, "The former shared-connection write endpoint is disabled");
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            Guid firstRunId = await coordinator.EnqueueAsync(first.Id, CancellationToken.None);
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await RejectAsync(async () =>
            {
                await coordinator.EnqueueAsync(first.Id, CancellationToken.None);
            }, "Duplicate running jobs are rejected");
            Guid secondRunId = await coordinator.EnqueueAsync(second.Id, CancellationToken.None);
            await jobs.SaveAsync(second with
            {
                Name = "Changed after queueing",
                Connection = second.Connection! with { Server = "changed-after-queueing" }
            }, CancellationToken.None, "ReplacementSecondSecret");
            await jobs.DeleteAsync(first, CancellationToken.None);
            engine.Release.TrySetResult();
            var firstRun = await WaitForRunAsync(runs, firstRunId);
            var secondRun = await WaitForRunAsync(runs, secondRunId);
            Check(firstRun.Status == RunStatus.Failed && firstRun.Databases[0].Backup == StageStatus.Succeeded
                && firstRun.Databases[0].Verification == StageStatus.Failed,
                "Verification failure retains completed primary-backup evidence");
            Check(firstRun.Job.Name == first.Name && secondRun.Job.Name == second.Name,
                "Editing or deleting jobs does not change run snapshots");
            Check(firstRun.Connection.Server == "first-server" && secondRun.Connection.Server == "second-server"
                && engine.ObservedPassword == "SecondJobSecret",
                "Queued runs retain their original connection and protected credential snapshot");
            Check(engine.ExecutionCount == 2, "Queued jobs execute once each under the serial worker");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        var interrupted = new BackupRun(Guid.NewGuid(), second,
            new ConnectionConfiguration("Fictional", "fictional-server", true, null), DateTimeOffset.UtcNow,
            RunStatus.Running, [new DatabaseBackupResult("TestDatabase", StageStatus.Running, StageStatus.Pending)], "Running");
        await runs.AddAsync(interrupted, null, CancellationToken.None);
        await runs.ReconcileInterruptedAsync(CancellationToken.None);
        var recovered = (await runs.ListAsync(CancellationToken.None)).Single(run => run.Id == interrupted.Id);
        Check(recovered.Status == RunStatus.Interrupted && recovered.Databases[0].Backup == StageStatus.Unknown,
            "Restart reconciliation preserves uncertain SQL outcomes and never retries them");

        var disabled = await jobs.GetAsync(second.Id, CancellationToken.None);
        await jobs.SaveAsync(disabled with
        {
            Enabled = false,
            Connection = disabled.Connection! with { UseWindowsAuthentication = true, UserName = null }
        }, CancellationToken.None);
        var windowsSnapshot = await secrets.ReadSnapshotAsync(second.Id, CancellationToken.None);
        Check(windowsSnapshot.ProtectedPassword is null && !windowsSnapshot.Configuration.HasPassword,
            "Switching one job to Windows authentication removes only that job's SQL password");
        using var restarted = new BackupCoordinator(configuration, jobs, runs, secrets,
            new SimulatedSqlEngine(), NullLogger<BackupCoordinator>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            await RejectAsync(async () =>
            {
                await restarted.EnqueueAsync(second.Id, CancellationToken.None);
            }, "Disabled jobs cannot be manually queued");
        }
        finally
        {
            await restarted.StopAsync(CancellationToken.None);
        }
    }

    private static async Task CheckLegacyJobMigrationAsync(ConfigurationStore store, JobStore jobs, ConnectionSecrets secrets)
    {
        var legacy = new BackupJob(Guid.NewGuid(), "Legacy job", ["TestDatabase"], @"C:\FictionalBackups", Revision: 1);
        await using (var connection = new SqliteConnection(store.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Jobs (Id, Revision, Payload) VALUES ($id, 1, $payload); PRAGMA user_version = 2;";
            command.Parameters.AddWithValue("$id", legacy.Id.ToString());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(legacy));
            await command.ExecuteNonQueryAsync();
        }

        await store.InitializeAsync(CancellationToken.None);
        var migrated = await jobs.GetAsync(legacy.Id, CancellationToken.None);
        Check(migrated.Connection?.Server == "first-server" && migrated.Revision == 2
            && ConnectionSecrets.UnprotectPassword(await secrets.ReadSnapshotAsync(migrated.Id, CancellationToken.None)) == "FictionalOriginalSecret",
            "Schema 2 jobs inherit an independent copy of legacy connection metadata and protected credentials");
        await jobs.DeleteAsync(migrated, CancellationToken.None);
    }

    private static async Task<BackupRun> WaitForRunAsync(RunStore store, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var run = (await store.ListAsync(timeout.Token)).Single(item => item.Id == id);
            if (run.Status is not (RunStatus.Queued or RunStatus.Running))
            {
                return run;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task RejectAsync(Func<Task> action, string description)
    {
        try
        {
            await action();
        }
        catch (ArgumentException)
        {
            Check(true, description);
            return;
        }

        throw new InvalidOperationException(description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }

        Console.WriteLine("PASS: " + description);
    }
}
