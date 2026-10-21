using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class OperationsChecks
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.OperationsTests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationStore(directory);
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var history = new HistoryStore(configuration);
        var notifications = new NotificationStore(configuration);
        var connection = new ConnectionConfiguration("Fixture", "fictional-private-server", true, null);
        var first = new BackupJob(Guid.NewGuid(), "Private customer job", ["TestDatabase"], @"C:\FictionalPrivatePath",
            Connection: connection, Notifications: new JobNotificationPolicy(true, true));
        var second = first with { Id = Guid.NewGuid(), Name = "Other job", Notifications = new JobNotificationPolicy(false, false) };
        await jobs.SaveAsync(first, CancellationToken.None);
        await jobs.SaveAsync(second, CancellationToken.None);
        first = await jobs.GetAsync(first.Id, CancellationToken.None);
        second = await jobs.GetAsync(second.Id, CancellationToken.None);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var fixtures = new List<BackupRun>();
        for (int index = 0; index < 45; index++)
        {
            var owner = index % 2 == 0 ? first : second;
            var type = (BackupType)(index % 3);
            bool success = index % 4 == 0;
            var result = new DatabaseBackupResult("TestDatabase", StageStatus.Succeeded,
                success ? StageStatus.Succeeded : StageStatus.Failed, @"C:\FictionalPrivatePath\customer-secret.bak",
                Message: "Private diagnostic text must never enter notifications",
                Copies: success ? [] : [new ArtifactCopy(@"\\private-share\backups", "private-artifact", StageStatus.Failed, "Private copy detail")]);
            var run = new BackupRun(Guid.NewGuid(), owner, connection, now.AddMinutes(-index), RunStatus.Running,
                [result], "Private run detail", Type: type);
            await runs.AddAsync(run, null, CancellationToken.None);
            run = run with { Status = success ? RunStatus.Succeeded : RunStatus.Failed, CompletedUtc = run.CreatedUtc.AddSeconds(1) };
            await runs.UpdateAsync(run, CancellationToken.None);
            fixtures.Add(run);
        }

        var page = await history.QueryAsync(new HistoryQuery(Limit: 20), CancellationToken.None);
        var next = await history.QueryAsync(new HistoryQuery(Offset: 20, Limit: 20), CancellationToken.None);
        var last = await history.QueryAsync(new HistoryQuery(Offset: 40, Limit: 20), CancellationToken.None);
        Check(page.Items.Length == 20 && page.HasMore && next.Items.Length == 20 && next.HasMore
            && last.Items.Length == 5 && !last.HasMore && !page.Items.Select(item => item.Id).Intersect(next.Items.Select(item => item.Id)).Any(),
            "History paging reaches older executions without duplicating rows");
        var filtered = await history.QueryAsync(new HistoryQuery(first.Id, RunStatus.Failed, BackupType.Log,
            "TestDatabase", now.AddMinutes(-35), now.AddMinutes(-5), Limit: 50), CancellationToken.None);
        var expected = fixtures.Where(item => item.Job.Id == first.Id && item.Status == RunStatus.Failed
            && item.Type == BackupType.Log && item.CreatedUtc >= now.AddMinutes(-35) && item.CreatedUtc < now.AddMinutes(-5));
        Check(filtered.Items.Select(item => item.Id).SequenceEqual(expected.Select(item => item.Id)),
            "Job, state, type, database and UTC date filters are applied together before pagination");
        var injection = await history.QueryAsync(new HistoryQuery(Database: "' OR 1=1 --"), CancellationToken.None);
        Check(injection.Items.Length == 0, "History database filters are parameterized exact matches");
        await RejectAsync(() => history.QueryAsync(new HistoryQuery(FromUtc: now, ToUtc: now.AddDays(-1)), CancellationToken.None),
            "Invalid history dates are rejected with a validation response");
        Check((await history.GetAsync(fixtures[0].Id, CancellationToken.None)).Databases[0].Copies is not null,
            "Full run details preserve separate backup, verification and copy evidence");

        var snapshot = await new OperationsStore(jobs, runs).ReadAsync(first.Id, CancellationToken.None);
        Check(snapshot.Databases.Single().LastBackupUtc.HasValue
            && snapshot.Databases.Single().Backup == StageStatus.Succeeded,
            "Database health reports created-backup evidence independently of later stage outcomes");
        var switched = OperationalProjection.Create([first with { Connection = connection with { Server = "another-server" } }],
            fixtures.ToArray(), first.Id, now);
        Check(switched.Databases.Single().LastBackupUtc is null && switched.Jobs.Single().LastStatus is null,
            "Changing a job's SQL server does not inherit the previous server's healthy status");
        var failedProjection = OperationalProjection.Create([first], fixtures.Skip(2).ToArray(), first.Id, now);
        Check(failedProjection.Databases.Single().Backup == StageStatus.Succeeded
            && failedProjection.Databases.Single().Verification == StageStatus.Failed
            && failedProjection.Databases.Single().CopyStatus == "Failed",
            "Verification and copy failures remain visible beside primary success");

        var delivered = await notifications.ListAsync(CancellationToken.None);
        Check(delivered.Items.Length == fixtures.Count(item => item.Job.Id == first.Id)
            && delivered.UnreadCount == delivered.Items.Length,
            "Local notifications are durably delivered with the selected job's success/failure policy");
        string encoded = JsonSerializer.Serialize(delivered.Items);
        Check(!encoded.Contains(first.Name) && !encoded.Contains(first.Destination)
            && !encoded.Contains(connection.Server) && !encoded.Contains("Private run") && !encoded.Contains("Private copy")
            && !encoded.Contains("TestDatabase"), "Notification payloads exclude names, paths, server details and raw errors");
        await runs.UpdateAsync(fixtures[0], CancellationToken.None);
        Check((await notifications.ListAsync(CancellationToken.None)).Items.Length == delivered.Items.Length,
            "Repeated terminal updates cannot deliver duplicate run notifications");
        await notifications.MarkReadAsync(delivered.Items[0].EventKey, CancellationToken.None);
        var reopened = new NotificationStore(new ConfigurationStore(directory));
        Check((await reopened.ListAsync(CancellationToken.None)).UnreadCount == delivered.UnreadCount - 1,
            "Notification delivery and read state survive reopening the store");

        await CheckSchemaUpgradeAsync(directory, first, fixtures[0]);
        await CheckAtomicDeliveryAsync(configuration, runs, history, first, now);
        var audits = new CleanupAuditStore(configuration);
        await audits.AddAsync(new CleanupAudit(Guid.NewGuid(), now, first.Id, @"C:\PrivateCustomerPath\secret.bak",
            "Failed", "Private cleanup reason"), CancellationToken.None);
        var cleanupAlert = (await notifications.ListAsync(CancellationToken.None)).Items.Single(item => item.EventKey.StartsWith("cleanup/"));
        Check(!cleanupAlert.Message.Contains("Private") && cleanupAlert.RunId is null,
            "Cleanup failures deliver a distinct redacted local alert");

        await CheckSaveValidationAsync(configuration, jobs, runs, first);
        var probes = await new StorageDiagnostics().CheckAsync(first with
        {
            Destination = directory,
            Storage = new StoragePolicy(SecondaryFolder: Path.Combine(directory, "missing-share"))
        }, CancellationToken.None);
        Check(probes.Single(item => item.Name == "Service source folder").Status == StageStatus.Succeeded
            && probes.Single(item => item.Name == "Secondary folder").Status == StageStatus.Failed
            && Directory.GetFiles(directory, "AbaSqlBackup-access-*.tmp").Length == 0,
            "Service diagnostics distinguish accessible and missing folders and remove only their own probe");
        for (int index = 0; index < 105; index++)
        {
            await audits.AddAsync(new CleanupAudit(Guid.NewGuid(), now.AddMinutes(index), first.Id,
                "Private fixture path", "Failed", "Fixture"), CancellationToken.None);
        }

        var crowded = await notifications.ListAsync(CancellationToken.None);
        Check(crowded.UnreadCount > 100 && crowded.Items.Length == 100 && crowded.Items.All(item => item.ReadUtc is null),
            "Unread alerts are prioritized when the inbox exceeds its display limit");
        await notifications.MarkAllReadAsync(CancellationToken.None);
        Check((await notifications.ListAsync(CancellationToken.None)).UnreadCount == 0,
            "Explicit mark-all-read acknowledges older notifications beyond the first page");
    }

    private static async Task CheckSchemaUpgradeAsync(string directory, BackupJob job, BackupRun run)
    {
        var legacy = new ConfigurationStore(Path.Combine(directory, "schema5-upgrade"));
        await legacy.InitializeAsync(CancellationToken.None);
        await new JobStore(legacy).SaveAsync(job with { Revision = 0 }, CancellationToken.None);
        await new RunStore(legacy).AddAsync(run, null, CancellationToken.None);
        await using (var connection = new SqliteConnection(legacy.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Jobs SET Payload = json_remove(Payload, '$.Notifications');
                UPDATE Runs SET Payload = json_remove(Payload, '$.Job.Notifications');
                DROP TABLE Notifications;
                DROP TABLE RunEvents;
                PRAGMA user_version = 5;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await legacy.InitializeAsync(CancellationToken.None);
        var loadedJob = await new JobStore(legacy).GetAsync(job.Id, CancellationToken.None);
        var loadedRun = await new HistoryStore(legacy).GetAsync(run.Id, CancellationToken.None);
        Check(loadedJob.Name == job.Name && loadedJob.Notifications is null && JsonSerializer.Serialize(loadedRun.Databases) == JsonSerializer.Serialize(run.Databases)
            && (await new NotificationStore(legacy).ListAsync(CancellationToken.None)).UnreadCount == 0,
            "Schema 5 upgrades without rewriting existing jobs/runs or generating retrospective alerts");
    }

    private static async Task CheckAtomicDeliveryAsync(ConfigurationStore store, RunStore runs, HistoryStore history,
        BackupJob job, DateTimeOffset now)
    {
        var run = new BackupRun(Guid.NewGuid(), job, job.Connection!, now, RunStatus.Running,
            [new DatabaseBackupResult("TestDatabase", StageStatus.Running, StageStatus.Pending)], "Fixture");
        await runs.AddAsync(run, null, CancellationToken.None);
        await using var connection = new SqliteConnection(store.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FixtureRejectNotification BEFORE INSERT ON Notifications BEGIN SELECT RAISE(ABORT, 'Fixture failure'); END;";
        await command.ExecuteNonQueryAsync();
        try
        {
            await runs.UpdateAsync(run with { Status = RunStatus.Failed }, CancellationToken.None);
            throw new InvalidOperationException("Expected durable delivery failure.");
        }
        catch (SqliteException)
        {
            Check((await history.GetAsync(run.Id, CancellationToken.None)).Status == RunStatus.Running,
                "A failed notification write rolls back terminal run state in the same transaction");
        }
        finally
        {
            command.CommandText = "DROP TRIGGER FixtureRejectNotification;";
            await command.ExecuteNonQueryAsync();
        }

        await runs.ReconcileInterruptedAsync(CancellationToken.None);
        Check((await new NotificationStore(store).ListAsync(CancellationToken.None)).Items.Any(item => item.RunId == run.Id
            && item.Title.Contains("Interrupted")), "Restart reconciliation durably delivers an interrupted-run alert without replaying SQL");
    }

    private static async Task CheckSaveValidationAsync(ConfigurationStore store, JobStore jobs, RunStore runs, BackupJob job)
    {
        var secrets = new ConnectionSecrets(store);
        var engine = new SimulatedSqlEngine { RecoveryModel = "SIMPLE" };
        var logger = new RecordingLogger<ManagementHandler>();
        using var coordinator = new BackupCoordinator(store, jobs, runs, secrets, engine, NullLogger<BackupCoordinator>.Instance);
        var handler = new ManagementHandler(jobs, runs, secrets, engine, new DestinationDiagnostics(), coordinator, logger,
            history: new HistoryStore(store), operations: new OperationsStore(jobs, runs),
            notifications: new NotificationStore(store));
        var response = await handler.HandleAsync(new ServiceRequest("QueryHistory", History: new HistoryQuery(job.Id)), CancellationToken.None);
        Check(response.History?.Items.All(item => item.JobId == job.Id) == true,
            "Management dispatch exposes the filtered history contract used by the desktop");
        var notificationResponse = await handler.HandleAsync(new ServiceRequest("Notifications"), CancellationToken.None);
        Check(notificationResponse.Notifications?.UnreadCount > 0,
            "Management dispatch delivers the persisted notification inbox");
        var schedule = new BackupSchedule(true, "UTC", new DateTime(2026, 10, 21), 1440, 0, 15, Enum.GetValues<DayOfWeek>(), []);
        var failed = await handler.HandleAsync(new ServiceRequest("SaveJob", Password: "NeverLogThisSecret",
            Job: job with { Schedule = schedule }), CancellationToken.None);
        Check(!failed.Success && failed.ErrorCode == "ScheduleLogRecoveryRequired"
            && failed.Message.Contains("SIMPLE") && failed.Message.Contains("Disable Transaction Log"),
            "Save Job explains exactly why a SIMPLE database cannot use a Log schedule");
        Check((await jobs.GetAsync(job.Id, CancellationToken.None)).Schedule is null,
            "Failed schedule validation preserves the saved job and revision");
        Check(logger.Entries.Any(item => item.Contains("SaveJob.Schedule") && item.Contains("ScheduleLogRecoveryRequired"))
            && logger.Entries.All(item => !item.Contains("NeverLogThisSecret") && !item.Contains(job.Name)
                && !item.Contains(job.Connection!.Server) && !item.Contains("TestDatabase")),
            "Management logs expose a useful stage/code without credentials or customer details");
        var saved = await handler.HandleAsync(new ServiceRequest("SaveJob", Job: job with
        { Schedule = schedule with { LogIntervalMinutes = 0 } }), CancellationToken.None);
        Check(saved.Success && (await jobs.GetAsync(job.Id, CancellationToken.None)).Schedule?.Enabled == true,
            "A valid Full-only job saves under SIMPLE recovery without changing the database");
        var master = job with { Databases = ["master"], Schedule = schedule with { DifferentialIntervalMinutes = 60, LogIntervalMinutes = 0 } };
        try
        {
            JobScheduleValidator.Validate(master, new ServerInspection("17", "Fixture", true, "Fixture",
                [new DatabaseInfo("master", "ONLINE", "SIMPLE", true, "Ready")]));
            throw new InvalidOperationException("Expected master Full-only validation.");
        }
        catch (JobValidationException exception)
        {
            Check(exception.Code == "ScheduleMasterFullOnly", "Master Differential validation has a distinct actionable code");
        }

        engine.PreflightFailures = 1;
        var persisted = await jobs.GetAsync(job.Id, CancellationToken.None);
        var disabled = await handler.HandleAsync(new ServiceRequest("SaveJob", Job: persisted with
        { Enabled = false, Schedule = schedule }), CancellationToken.None);
        Check(disabled.Success && engine.PreflightFailures == 1,
            "Disabling a job never depends on reaching SQL or enabling its inactive backup types");
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
        }
        catch (ArgumentException)
        {
            Check(true, message);
            return;
        }

        throw new InvalidOperationException(message);
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
