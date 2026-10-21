using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;
using SqlBackup.Setup;

namespace SqlBackup.Tests;

public static class SetupChecks
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.SetupTests", Guid.NewGuid().ToString("N"));
        await CheckDatabaseRecoveryAsync(directory);
        var configuration = new ConfigurationStore(Path.Combine(directory, "state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var secrets = new ConnectionSecrets(configuration);
        var job = new BackupJob(Guid.NewGuid(), "Maintenance fixture", ["TestDatabase"], @"C:\FictionalBackups",
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null));
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var engine = new SimulatedSqlEngine { BlockExecution = true };
        using var coordinator = new BackupCoordinator(configuration, jobs, runs, secrets, engine, NullLogger<BackupCoordinator>.Instance);
        var handler = new ManagementHandler(jobs, runs, secrets, engine, new DestinationDiagnostics(), coordinator,
            NullLogger<ManagementHandler>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            Guid id = await coordinator.EnqueueAsync(job.Id, CancellationToken.None);
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = await handler.HandleAsync(new ServiceRequest("BeginMaintenance"), CancellationToken.None);
            Check(!busy.Success && !coordinator.IsMaintenanceActive,
                "Installer maintenance refuses an active backup without stopping it or disabling future schedules");
            engine.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await runs.GetAsync(id, timeout.Token)).Status is RunStatus.Running or RunStatus.Queued)
            {
                await Task.Delay(20, timeout.Token);
            }

            await jobs.SaveAsync(job with
            {
                Schedule = new BackupSchedule(true, "UTC", DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1), DateTimeKind.Unspecified), 1440, 0, 0,
                    Enum.GetValues<DayOfWeek>(), [])
            }, timeout.Token);

            // Final persistence precedes activeJobs removal in the worker.
            ServiceResponse maintenance;
            do
            {
                maintenance = await handler.HandleAsync(new ServiceRequest("BeginMaintenance"), timeout.Token);
                if (!maintenance.Success)
                {
                    await Task.Delay(20, timeout.Token);
                }
            }
            while (!maintenance.Success);
            Check(coordinator.IsMaintenanceActive && !(await handler.HandleAsync(
                new ServiceRequest("SaveJob", Job: job with { Name = "Blocked change" }), timeout.Token)).Success,
                "Accepted maintenance durably blocks configuration changes while preserving read access");
            try
            {
                await coordinator.EnqueueAsync(job.Id, timeout.Token);
                throw new InvalidOperationException("Expected maintenance queue rejection.");
            }
            catch (ArgumentException)
            {
                Check(true, "Maintenance prevents a manual or scheduled enqueue racing with installer shutdown");
            }

            var schedules = new ScheduleStore(configuration);
            await new ScheduleWorker(configuration, jobs, schedules, coordinator, NullLogger<ScheduleWorker>.Instance)
                .TickAsync(DateTimeOffset.UtcNow, timeout.Token);
            Check(await schedules.ReadAsync(job.Id, timeout.Token) is null,
                "Maintenance does not advance schedule cursors or consume missed occurrences");
            Check((await handler.HandleAsync(new ServiceRequest("SetupReadiness"), timeout.Token)).Success,
                "Setup readiness validates storage and credentials without SQL/NAS access");
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }

        using var restarted = new BackupCoordinator(configuration, jobs, runs, secrets, new SimulatedSqlEngine(),
            NullLogger<BackupCoordinator>.Instance);
        Check(restarted.IsMaintenanceActive, "A pending setup marker survives worker recreation and keeps execution paused");
        string source = Path.Combine(directory, "files");
        string destination = Path.Combine(directory, "recovery");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "fixture.txt"), "Original payload");
        SetupFiles.CopyTree(source, destination);
        Check(await File.ReadAllTextAsync(Path.Combine(destination, "fixture.txt")) == "Original payload",
            "Recovery copies preserve managed application payloads");
        try
        {
            SetupFiles.DeleteManagedTree(source, destination);
            throw new InvalidOperationException("Expected recovery path guard.");
        }
        catch (IOException)
        {
            Check(File.Exists(Path.Combine(source, "fixture.txt")), "Rollback deletion refuses paths outside its verified managed root");
        }
    }

    private static async Task CheckDatabaseRecoveryAsync(string directory)
    {
        string sourceDirectory = Directory.CreateDirectory(Path.Combine(directory, "database")).FullName;
        string database = Path.Combine(sourceDirectory, "configuration.db");
        string snapshot = Path.Combine(directory, "snapshot.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Fixture (Value TEXT NOT NULL); INSERT INTO Fixture VALUES ('original'); PRAGMA user_version=8;";
            await command.ExecuteNonQueryAsync();
            SetupDatabase.Snapshot(database, snapshot);
            command.CommandText = "UPDATE Fixture SET Value='failed upgrade'; PRAGMA user_version=999;";
            await command.ExecuteNonQueryAsync();
        }

        SetupDatabase.Restore(sourceDirectory, snapshot);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM Fixture;";
            Check(await command.ExecuteScalarAsync() as string == "original",
                "SQLite recovery snapshot includes committed WAL data and restores pre-upgrade configuration");
            command.CommandText = "PRAGMA user_version;";
            Check(Convert.ToInt32(await command.ExecuteScalarAsync()) == 8,
                "Rollback restores the original schema together with its matching data");
        }

        string damaged = Path.Combine(directory, "damaged.db");
        await File.WriteAllTextAsync(damaged, "Not an SQLite database");
        try
        {
            SetupDatabase.Restore(sourceDirectory, damaged);
            throw new InvalidOperationException("Expected corrupt snapshot rejection.");
        }
        catch (SqliteException)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM Fixture;";
            Check(await command.ExecuteScalarAsync() as string == "original",
                "Invalid recovery evidence is rejected before replacing the current database");
        }
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
