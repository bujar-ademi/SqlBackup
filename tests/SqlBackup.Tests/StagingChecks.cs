using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class StagingChecks
{
    public static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.StagingTests", Guid.NewGuid().ToString("N"));
        string staging = Directory.CreateDirectory(Path.Combine(directory, "staging")).FullName;
        string final = Directory.CreateDirectory(Path.Combine(directory, "final")).FullName;
        string secondary = Directory.CreateDirectory(Path.Combine(directory, "secondary")).FullName;
        var configuration = new ConfigurationStore(Path.Combine(directory, "state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var job = new BackupJob(Guid.NewGuid(), "Staging fixture", ["TestDatabase"], final, CopyOnly: false,
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null),
            Storage: new StoragePolicy(7, secondary), TemporaryFolder: staging);
        CheckNames(job);
        await CheckMigrationAsync(directory, job);
        await CheckCredentialsAsync(configuration, job);
        await CheckTransfersAsync(job);
        await CheckWorkerAndRetentionAsync(configuration, job);
    }

    private static async Task CheckMigrationAsync(string directory, BackupJob template)
    {
        var configuration = new ConfigurationStore(Path.Combine(directory, "schema7-state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var legacy = template with { Id = Guid.NewGuid(), TemporaryFolder = null };
        await jobs.SaveAsync(legacy, CancellationToken.None);
        legacy = await jobs.GetAsync(legacy.Id, CancellationToken.None);
        string before = JsonSerializer.Serialize(legacy);
        await using (var connection = new SqliteConnection(configuration.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE JobStorageSecrets; DROP TABLE RunStorageSecrets; PRAGMA user_version = 7;";
            await command.ExecuteNonQueryAsync();
        }

        await configuration.InitializeAsync(CancellationToken.None);
        Check(JsonSerializer.Serialize(await jobs.GetAsync(legacy.Id, CancellationToken.None)) == before
            && (await new StorageSecrets(configuration).ReadJobAsync(legacy.Id, CancellationToken.None)).Destination is null,
            "Schema 7 upgrades without rewriting an existing direct-write job or inventing share credentials");
    }

    private static void CheckNames(BackupJob job)
    {
        Guid id = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 10, 20, 18, 14, 25, TimeSpan.Zero).AddTicks(1234567);
        string path = BackupSql.CreateArtifactPath(job, "TestDatabase", id, timestamp);
        Check(Path.GetDirectoryName(path) == job.TemporaryFolder && !path.Contains(id.ToString("N"))
            && Path.GetFileName(path) == "TestDatabase_Full_2026-10-20_18-14-25-1234567Z.bak",
            "SQL paths target the job's temporary folder using readable UTC names without a run GUID");
        var collisions = job with { Databases = ["a?", "a*", "A_", "a_2", new string('z', 60), new string('z', 59) + "x"] };
        string[] names = collisions.Databases.Select(database =>
            BackupSql.CreateArtifactPath(collisions, database, id, timestamp)).ToArray();
        Check(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length,
            "Sanitized, truncated and case-colliding database names receive safe numbered suffixes");
        var legacy = job with { TemporaryFolder = null };
        string legacyPath = BackupSql.CreateArtifactPath(legacy, "TestDatabase", id, timestamp);
        Check(legacyPath.Contains(id.ToString("N")) && Path.GetDirectoryName(legacyPath) == job.Destination
            && JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(legacy))?.TemporaryFolder is null,
            "Historical snapshots retain their original path/name derivation for retention evidence");
        Check(BackupSql.CreateArtifactPath(job, "TestDatabase", id, timestamp, BackupType.Log).EndsWith(".trn"),
            "Log backups retain their distinct TRN extension");
        try
        {
            JobValidation.Validate(job with { TemporaryFolder = job.Destination });
            throw new InvalidOperationException("Expected temporary/final path validation.");
        }
        catch (ArgumentException)
        {
            Check(true, "Temporary storage cannot alias the configured final folder");
        }
    }

    private static async Task CheckCredentialsAsync(ConfigurationStore configuration, BackupJob template)
    {
        var jobs = new JobStore(configuration);
        var secrets = new StorageSecrets(configuration);
        var job = template with
        {
            Id = Guid.NewGuid(),
            Destination = @"\\fictional-nas\backups",
            Storage = new StoragePolicy(ServiceSourceFolder: @"\\fictional-sql\staging",
                DestinationUserName: @"NAS\fixture", SourceUserName: @"SQLHOST\fixture")
        };
        const string password = "Fictional-NAS-Secret-123!";
        await jobs.SaveAsync(job, CancellationToken.None, storagePasswords: new StoragePasswords(password, Source: "Fictional-Source-Secret"));
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var access = await secrets.PrepareAsync(job, null, CancellationToken.None);
        using var originalIdentity = WindowsIdentity.GetCurrent();
        bool impersonation = await ShareAccess.RunAsync(access.Source, async () =>
        {
            await Task.Yield();
            return await ShareAccess.RunAsync(access.Destination, async () =>
            {
                await Task.Yield();
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User == originalIdentity.User && identity.ImpersonationLevel != TokenImpersonationLevel.None;
            });
        });
        using var restoredIdentity = WindowsIdentity.GetCurrent();
        Check(impersonation && restoredIdentity.User == originalIdentity.User
            && restoredIdentity.ImpersonationLevel == originalIdentity.ImpersonationLevel,
            "Nested outbound share impersonation survives async yields and restores the original identity without contacting a NAS");
        bool defaultDestination = await ShareAccess.RunAsync(access.Source, () => ShareAccess.RunAsync(null, async () =>
        {
            await Task.Yield();
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User == originalIdentity.User && identity.ImpersonationLevel == originalIdentity.ImpersonationLevel;
        }));
        Check(defaultDestination, "A service-account destination does not inherit the temporary source's explicit network identity");
        byte[] plaintext = ProtectedData.Unprotect(access.Destination!.ProtectedPassword, null, DataProtectionScope.CurrentUser);
        try
        {
            Check(Encoding.UTF8.GetString(plaintext) == password && access.Source!.ProtectedPassword.Length > 0,
                "NAS and remote source credentials are independently protected under the service identity");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        await using (var connection = new SqliteConnection(configuration.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM Jobs UNION ALL SELECT Payload FROM JobStorageSecrets;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Check(!reader.GetString(0).Contains(password), "Persisted job/storage payloads contain no plaintext NAS password");
            }
        }

        await ExpectArgumentAsync(() => jobs.SaveAsync(job with { Id = Guid.NewGuid(), Revision = 0 }, CancellationToken.None),
            "A new job cannot borrow another job's NAS credentials");
        await ExpectArgumentAsync(() => jobs.SaveAsync(job with { Destination = @"\\another-nas\backups" }, CancellationToken.None),
            "Changing the destination requires replacement NAS credentials");
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var run = new BackupRun(Guid.NewGuid(), job, job.Connection!, DateTimeOffset.UtcNow, RunStatus.Queued, [], "Fixture");
        await new RunStore(configuration).AddAsync(run, null, CancellationToken.None, access);
        await jobs.DeleteAsync(job, CancellationToken.None);
        Check((await secrets.ReadJobAsync(job.Id, CancellationToken.None)).Destination is null
            && (await secrets.ReadRunAsync(run.Id, CancellationToken.None)).Destination!.ProtectedPassword.SequenceEqual(access.Destination.ProtectedPassword),
            "Deleting a job removes its active NAS secret while queued/historical run credentials remain immutable");
        var missingCredentials = await new ArtifactTransfer().ProcessAsync(job,
            new DatabaseBackupResult(job.Databases[0], StageStatus.Succeeded, StageStatus.Succeeded,
                Path.Combine(job.TemporaryFolder!, "fixture.bak")), CancellationToken.None);
        Check(missingCredentials.Copies?.Single().Status == StageStatus.Failed,
            "Missing explicit share snapshots fail closed without falling back to the service account");
    }

    private static async Task CheckTransfersAsync(BackupJob job)
    {
        var transfer = new ArtifactTransfer();
        var result = await CreateArtifactAsync(job, DateTimeOffset.UtcNow, "100", "110");
        var copied = await transfer.ProcessAsync(job, result, CancellationToken.None);
        Check(copied.Copies?.Length == 2 && copied.Copies.All(copy => copy.Status == StageStatus.Succeeded)
            && File.Exists(result.ArtifactPath), "All final destinations receive verified copies before temporary cleanup");
        var verificationFailure = await transfer.CleanupStagingAsync(job, copied with { Verification = StageStatus.Failed }, CancellationToken.None);
        Check(verificationFailure.StagingCleanup == StageStatus.Skipped && File.Exists(result.ArtifactPath),
            "Failed SQL verification preserves the temporary backup even when transfer succeeds");
        string finalPath = copied.Copies![0].Path;
        await File.AppendAllTextAsync(finalPath, "tampered");
        var changed = await transfer.CleanupStagingAsync(job, copied, CancellationToken.None);
        Check(changed.StagingCleanup == StageStatus.Failed && File.Exists(result.ArtifactPath),
            "A changed final copy prevents removal of its temporary source");
        var collision = await transfer.ProcessAsync(job, result, CancellationToken.None);
        Check(collision.Copies!.All(copy => copy.Status == StageStatus.Failed)
            && (await File.ReadAllTextAsync(finalPath)).EndsWith("tampered"), "Final-file collisions are failures and never overwrite existing backups");
        var unavailable = await transfer.ProcessAsync(job with { Destination = Path.Combine(job.Destination, "missing") }, result, CancellationToken.None);
        var preserved = await transfer.CleanupStagingAsync(job, unavailable, CancellationToken.None);
        Check(preserved.Backup == StageStatus.Succeeded && preserved.StagingCleanup == StageStatus.Skipped
            && File.Exists(result.ArtifactPath), "Failed required destination keeps primary completion and the temporary backup");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await transfer.ProcessAsync(job, result, cancellation.Token);
            throw new InvalidOperationException("Expected copy cancellation.");
        }
        catch (OperationCanceledException)
        {
            Check(File.Exists(result.ArtifactPath), "Cancelled transfer leaves the temporary backup available for inspection");
        }
    }

    private static async Task CheckWorkerAndRetentionAsync(ConfigurationStore configuration, BackupJob job)
    {
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var engine = new SimulatedSqlEngine { WriteArtifact = true };
        using var worker = new BackupCoordinator(configuration, jobs, runs, new ConnectionSecrets(configuration), engine,
            NullLogger<BackupCoordinator>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Guid id = await worker.EnqueueAsync(job.Id, timeout.Token);
            BackupRun run;
            do
            {
                await Task.Delay(20, timeout.Token);
                run = await runs.GetAsync(id, timeout.Token);
            }
            while (run.Status is RunStatus.Queued or RunStatus.Running);
            var result = run.Databases.Single();
            Check(run.Status == RunStatus.Succeeded && result.StagingCleanup == StageStatus.Succeeded
                && !File.Exists(result.ArtifactPath) && result.Copies!.All(copy => File.Exists(copy.Path)),
                "The serialized worker backs up to temporary storage, persists copies, then removes only the temporary file");
            var log = await new HistoryStore(configuration).ReadLogAsync(id, timeout.Token);
            int copyIndex = Array.FindLastIndex(log, item => item.Stage == "Copy" && item.Status == "Succeeded");
            int cleanupIndex = Array.FindIndex(log, item => item.Stage == "Temporary cleanup" && item.Status == "Running");
            Check(copyIndex >= 0 && cleanupIndex > copyIndex,
                "Durable copy completion events precede the temporary deletion intent");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // Only fixtures with complete chain metadata enter this retention test.
        var retentionJob = job with { Id = Guid.NewGuid(), Revision = 0 };
        await jobs.SaveAsync(retentionJob, CancellationToken.None);
        retentionJob = await jobs.GetAsync(retentionJob.Id, CancellationToken.None);
        var old = await SaveCompletedArtifactAsync(runs, retentionJob, DateTimeOffset.UtcNow.AddDays(-30), "100", "110");
        var anchor = await SaveCompletedArtifactAsync(runs, retentionJob, DateTimeOffset.UtcNow.AddDays(-10), "200", "210");
        var retention = new RetentionService(jobs, runs, new CleanupAuditStore(configuration), new ConnectionSecrets(configuration),
            new FixtureHistoryInspector(), new StorageSecrets(configuration));
        var preview = await retention.PreviewAsync(retentionJob.Id, CancellationToken.None);
        // The worker above shares a destination and deliberately lacks chain evidence; fail closed there.
        Check(preview.Decisions.All(item => !item.Delete), "Uncertain evidence from another job still blocks shared-location cleanup");
        var isolatedFinal = Directory.CreateDirectory(Path.Combine(job.Destination, "isolated")).FullName;
        var isolatedSecondary = Directory.CreateDirectory(Path.Combine(job.Storage!.SecondaryFolder!, "isolated")).FullName;
        retentionJob = retentionJob with { Destination = isolatedFinal, Storage = new StoragePolicy(7, isolatedSecondary) };
        await jobs.SaveAsync(retentionJob, CancellationToken.None);
        retentionJob = await jobs.GetAsync(retentionJob.Id, CancellationToken.None);
        old = await SaveCompletedArtifactAsync(runs, retentionJob, DateTimeOffset.UtcNow.AddDays(-29), "300", "310");
        anchor = await SaveCompletedArtifactAsync(runs, retentionJob, DateTimeOffset.UtcNow.AddDays(-9), "400", "410");
        preview = await retention.PreviewAsync(retentionJob.Id, CancellationToken.None);
        Check(preview.Decisions.Count(item => item.Delete) == 1 && !File.Exists(old.Databases[0].ArtifactPath),
            "Final-folder retention uses verified copy evidence after temporary files have been removed");
        await retention.ExecuteAsync(retentionJob.Id, preview.Token, CancellationToken.None);
        Check(!File.Exists(old.Databases[0].Copies![0].Path) && File.Exists(old.Databases[0].Copies![1].Path)
            && File.Exists(anchor.Databases[0].Copies![0].Path), "Reviewed retention deletes only the eligible final copy and preserves its secondary/base");
    }

    private static async Task<DatabaseBackupResult> CreateArtifactAsync(BackupJob job, DateTimeOffset created, string first, string last,
        Guid? runId = null)
    {
        string path = BackupSql.CreateArtifactPath(job, job.Databases[0], runId ?? Guid.NewGuid(), created);
        await File.WriteAllTextAsync(path, "Synthetic staging payload " + first);
        return new DatabaseBackupResult(job.Databases[0], StageStatus.Succeeded, StageStatus.Succeeded, path,
            BackupSetId: Guid.NewGuid().ToString(), FirstLsn: first, LastLsn: last, CheckpointLsn: first,
            DatabaseBackupLsn: first, DatabaseId: "identity", RecoveryForkId: "fork", HeaderCopyOnly: false,
            HasSingleRecoveryFork: true, HeaderType: BackupType.Full);
    }

    private static async Task<BackupRun> SaveCompletedArtifactAsync(RunStore runs, BackupJob job, DateTimeOffset created,
        string first, string last)
    {
        Guid id = Guid.NewGuid();
        var result = await CreateArtifactAsync(job, created, first, last, id);
        var transfer = new ArtifactTransfer();
        result = await transfer.ProcessAsync(job, result, CancellationToken.None);
        var run = new BackupRun(id, job, job.Connection!, created, RunStatus.Running, [result], "Synthetic fixture");
        await runs.AddAsync(run, null, CancellationToken.None);
        result = await transfer.CleanupStagingAsync(job, result, CancellationToken.None);
        run = run with { Status = RunStatus.Succeeded, Databases = [result], CompletedUtc = created };
        await runs.UpdateAsync(run, CancellationToken.None);
        return run;
    }

    private static async Task ExpectArgumentAsync(Func<Task> operation, string message)
    {
        try
        {
            await operation();
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
