using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class DestinationChecks
{
    public static async Task RunAsync()
    {
        CheckValidation();
        await CheckSegmentStreamAsync();
        await CheckRunAsync(false);
        await CheckRunAsync(true);
        await CheckRetentionAsync();
        await CheckMixedHistoryAsync();
    }

    private static void CheckValidation()
    {
        var azure = AzureDestination();
        DestinationValidation.Validate(azure);
        DestinationValidation.Validate(S3Destination());
        Reject(() => DestinationValidation.Validate(azure with { Location = azure.Location + "?sig=FictionalSecret" }),
            "SAS tokens cannot enter ordinary destination metadata");
        Reject(() => DestinationValidation.Validate(azure with { Location = "https://untrusted.example/container" }),
            "Cloud credentials cannot be sent to an arbitrary configured endpoint");
        Reject(() => DestinationValidation.Validate(azure with { Prefix = "../escape" }),
            "Cloud prefixes reject relative segments");
        var job = new BackupJob(Guid.NewGuid(), "Fixture", ["TestDatabase"], string.Empty,
            TemporaryFolder: @"C:\FixtureStaging", Destinations: [azure, azure with { Id = Guid.NewGuid() }]);
        Reject(() => JobValidation.Validate(job), "A job cannot configure the same cloud target twice");
        Check(BackupDestinations.Read(job with { Destinations = null, Destination = @"D:\Backups" }).Length == 1,
            "Legacy jobs can be represented as a destination list without rewriting historical snapshots");
    }

    private static async Task CheckSegmentStreamAsync()
    {
        using var source = new MemoryStream(Enumerable.Range(0, 100).Select(value => (byte)value).ToArray());
        using (var segment = new ReadOnlySegmentStream(source, 20, 10))
        {
            byte[] buffer = new byte[20];
            Check(await segment.ReadAsync(buffer) == 10 && buffer.Take(10).SequenceEqual(Enumerable.Range(20, 10).Select(value => (byte)value))
                && await segment.ReadAsync(buffer) == 0, "Multipart streams cannot read outside their selected part");
            segment.Position = 0;
            Check(segment.ReadByte() == 20, "An SDK retry seeks within the same multipart segment");
        }

        Check(source.CanRead, "Disposing a multipart segment preserves the source for later destinations");
    }

    private static async Task CheckRunAsync(bool cloudOnly)
    {
        string directory = NewDirectory();
        string staging = Directory.CreateDirectory(Path.Combine(directory, "staging")).FullName;
        string final = Directory.CreateDirectory(Path.Combine(directory, "final")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(directory, "second")).FullName;
        var configuration = new ConfigurationStore(Path.Combine(directory, "state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var azure = AzureDestination();
        var s3 = S3Destination();
        BackupDestination[] destinations = [azure, s3];
        if (!cloudOnly)
        {
            destinations = [new BackupDestination(Guid.NewGuid(), "Local", DestinationKind.Folder, final),
                new BackupDestination(Guid.NewGuid(), "Second local", DestinationKind.Folder, second), azure, s3];
        }

        var job = new BackupJob(Guid.NewGuid(), "Destination fixture", ["TestDatabase"], string.Empty,
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null),
            TemporaryFolder: staging, Destinations: destinations);
        DestinationPassword[] passwords = [new(azure.Id, "FictionalAzureSas"), new(s3.Id, "FictionalS3Secret")];
        await jobs.SaveAsync(job, CancellationToken.None, destinationPasswords: passwords);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        await CheckCredentialIsolationAsync(configuration, jobs, job, passwords);
        var remote = new RecordingDestinationStorage();
        if (cloudOnly)
        {
            remote.FailingDestinations.Add(s3.Id);
        }
        else
        {
            remote.TransientFailures[azure.Id] = 1;
        }

        var factory = new DestinationStorage(kind => kind == DestinationKind.Folder ? new FolderDestinationStorage() : remote);
        var engine = new SimulatedSqlEngine { WriteArtifact = true, DirectDestinationAvailable = !cloudOnly };
        using var coordinator = new BackupCoordinator(configuration, jobs, runs, new ConnectionSecrets(configuration),
            engine, NullLogger<BackupCoordinator>.Instance, factory);
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            Guid runId = await coordinator.EnqueueAsync(job.Id, CancellationToken.None);
            var run = await WaitAsync(runs, runId);
            var result = run.Databases.Single();
            Check(engine.ExecutionCount == 1 && run.Job.Destinations?.Length == destinations.Length,
                "Multiple destinations share exactly one SQL backup and immutable execution configuration");
            Check(result.DestinationTransfers?.Single(item => item.DestinationId == azure.Id).Status == StageStatus.Succeeded
                && remote.ObservedSecrets[azure.Id] == "FictionalAzureSas",
                "Azure destination receives its own protected credential and independent result");
            if (cloudOnly)
            {
                Check(run.Status == RunStatus.Failed && File.Exists(result.ArtifactPath)
                    && result.StagingCleanup == StageStatus.Skipped,
                    "A failed cloud destination preserves staging and another destination's successful receipt");
                string runBefore = JsonSerializer.Serialize(run.Job);
                var changed = job with { Destinations = destinations.Select(item => item.Id == s3.Id ? item with { UserName = "ChangedKeyId" } : item).ToArray() };
                await jobs.SaveAsync(changed, CancellationToken.None,
                    destinationPasswords: [new DestinationPassword(s3.Id, "ChangedFictionalSecret")]);
                remote.FailingDestinations.Clear();
                await Task.Delay(30);
                await coordinator.RetryDestinationsAsync(runId, CancellationToken.None);
                run = await WaitAsync(runs, runId);
                result = run.Databases.Single();
                Check(run.Status == RunStatus.Succeeded && engine.ExecutionCount == 1
                    && remote.UploadCounts[azure.Id] == 1 && remote.UploadCounts[s3.Id] == 2,
                    "Destination-only retry preserves successful uploads and never repeats SQL backup");
                Check(remote.ObservedSecrets[s3.Id] == "FictionalS3Secret" && JsonSerializer.Serialize(run.Job) == runBefore,
                    "A retry uses its own saved destination snapshot after the job credentials change");
                Check(result.StagingCleanup == StageStatus.Succeeded && !File.Exists(result.ArtifactPath),
                    "Cloud-only staging is removed after every receipt is persisted and confirmed");
            }
            else
            {
                Check(remote.UploadCounts[azure.Id] == 2
                    && result.DestinationTransfers?.Single(item => item.DestinationId == azure.Id).Attempts == 2,
                    "Transient destination errors retry transfer with a recorded attempt count");
                Check(run.Status == RunStatus.Succeeded && run.DirectDestinationId == destinations[0].Id
                    && !Directory.EnumerateFiles(staging).Any() && File.Exists(result.ArtifactPath)
                    && Directory.EnumerateFiles(second).Count() == 1 && result.DestinationTransfers?.Length == 4,
                    "A suitable local destination becomes the direct SQL output and feeds every other destination");
            }
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task CheckCredentialIsolationAsync(ConfigurationStore configuration, JobStore jobs,
        BackupJob job, DestinationPassword[] passwords)
    {
        var access = await new DestinationSecrets(configuration).PrepareAsync(job, null, CancellationToken.None);
        Check(access.Where(item => item.ProtectedSecret is not null).All(item =>
            !Encoding.UTF8.GetString(item.ProtectedSecret!).Contains("Fictional", StringComparison.Ordinal)),
            "Destination secrets are DPAPI protected rather than plaintext");
        try
        {
            await jobs.SaveAsync(job with { Id = Guid.NewGuid(), Revision = 0 }, CancellationToken.None);
            throw new InvalidOperationException("Another job must not inherit destination credentials.");
        }
        catch (ArgumentException)
        {
            Check(true, "Destination credentials cannot be borrowed by a new job");
        }

        await using var connection = new SqliteConnection(configuration.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        string payload = (string)(await command.ExecuteScalarAsync())!;
        Check(passwords.All(item => !payload.Contains(item.Secret!, StringComparison.Ordinal)),
            "Ordinary job metadata and management responses contain no destination secrets");
    }

    private static async Task CheckRetentionAsync()
    {
        string directory = NewDirectory();
        string staging = Directory.CreateDirectory(Path.Combine(directory, "staging")).FullName;
        var configuration = new ConfigurationStore(Path.Combine(directory, "state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var destination = AzureDestination() with { RetentionDays = 7 };
        var job = new BackupJob(Guid.NewGuid(), "Cloud retention", ["TestDatabase"], string.Empty, CopyOnly: false,
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null),
            TemporaryFolder: staging, Destinations: [destination]);
        await jobs.SaveAsync(job, CancellationToken.None, destinationPasswords: [new(destination.Id, "FictionalSas")]);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var credentials = await new DestinationSecrets(configuration).PrepareAsync(job, null, CancellationToken.None);
        var remote = new RecordingDestinationStorage();
        var factory = new DestinationStorage(_ => remote);
        foreach (int age in new[] { 60, 30, 1 })
        {
            var run = new BackupRun(Guid.NewGuid(), job, job.Connection!, DateTimeOffset.UtcNow.AddDays(-age), RunStatus.Succeeded,
                [], "Synthetic retention evidence", CompletedUtc: DateTimeOffset.UtcNow.AddDays(-age),
                SqlOutputFolder: staging, ServiceOutputFolder: staging);
            string fileName = Path.GetFileName(BackupArtifactPaths.SqlPath(run, "TestDatabase"));
            byte[] content = Encoding.UTF8.GetBytes("Synthetic backup " + age);
            string hash = Convert.ToHexString(SHA256.HashData(content));
            using var stream = new MemoryStream(content);
            var receipt = await remote.UploadAsync(credentials[0], stream,
                StorageObjectPaths.Key(run, fileName, destination), hash, CancellationToken.None);
            var result = new DatabaseBackupResult("TestDatabase", StageStatus.Succeeded, StageStatus.Succeeded,
                Path.Combine(staging, fileName), BackupSetId: run.Id.ToString(), FirstLsn: "100", LastLsn: "200",
                CheckpointLsn: "100", DatabaseBackupLsn: "100", DatabaseId: "FixtureDatabaseId",
                RecoveryForkId: "FixtureFork", HeaderCopyOnly: false, Sha256: hash, HasSingleRecoveryFork: true,
                HeaderType: BackupType.Full, StagingCleanup: StageStatus.Succeeded,
                DestinationTransfers: [new DestinationTransfer(destination.Id, StageStatus.Succeeded, receipt.Path,
                    "Synthetic confirmed object", hash, content.Length, receipt.EntityTag, receipt.VersionId)]);
            run = run with { Databases = [result] };
            await runs.AddAsync(run, null, CancellationToken.None, destinationAccess: credentials);
        }

        var inspector = new FixtureHistoryInspector();
        var retention = new RetentionService(jobs, runs, new CleanupAuditStore(configuration), new ConnectionSecrets(configuration),
            inspector, new StorageSecrets(configuration), configuration, factory);
        var preview = await retention.PreviewAsync(job.Id, CancellationToken.None);
        Check(preview.Decisions.Length == 3 && preview.Decisions.Count(item => item.Delete) == 1,
            "Cloud retention preserves the Full recovery anchor rather than deleting by file age");
        string eligible = preview.Decisions.Single(item => item.Delete).Artifact.Path;
        byte[] original = remote.Objects[eligible];
        remote.Objects[eligible] = "Changed cloud object"u8.ToArray();
        var changedPreview = await retention.PreviewAsync(job.Id, CancellationToken.None);
        Check(changedPreview.Decisions.All(item => !item.Delete), "Changed remote content blocks cloud deletion");
        remote.Objects[eligible] = original;
        inspector.HasExternal = true;
        var externalPreview = await retention.PreviewAsync(job.Id, CancellationToken.None);
        Check(externalPreview.Decisions.All(item => !item.Delete), "External SQL backup history blocks cloud deletion");
        inspector.HasExternal = false;
        preview = await retention.PreviewAsync(job.Id, CancellationToken.None);
        var outcomes = await retention.ExecuteAsync(job.Id, preview.Token, CancellationToken.None);
        Check(remote.Objects.Count == 2 && outcomes.Any(item => item.Outcome == "Deleted" && item.Path == eligible),
            "Reviewed cloud cleanup deletes only the eligible confirmed object and records the audit");
    }

    private static async Task<BackupRun> WaitAsync(RunStore runs, Guid runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        BackupRun run;
        do
        {
            await Task.Delay(20, timeout.Token);
            run = await runs.GetAsync(runId, timeout.Token);
        }
        while (run.Status is RunStatus.Queued or RunStatus.Running);
        return run;
    }

    private static async Task CheckMixedHistoryAsync()
    {
        string directory = NewDirectory();
        string folder = Directory.CreateDirectory(Path.Combine(directory, "backups")).FullName;
        string staging = Directory.CreateDirectory(Path.Combine(directory, "staging")).FullName;
        var configuration = new ConfigurationStore(Path.Combine(directory, "state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var destination = new BackupDestination(Guid.NewGuid(), "Mixed history", DestinationKind.Folder, folder, RetentionDays: 7);
        var job = new BackupJob(Guid.NewGuid(), "Migrated job", ["TestDatabase"], folder, CopyOnly: false,
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null),
            TemporaryFolder: staging, Destinations: [destination]);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var credentials = await new DestinationSecrets(configuration).PrepareAsync(job, null, CancellationToken.None);
        Guid oldestRunId = Guid.Empty;
        foreach (int age in new[] { 90, 30, 2 })
        {
            bool legacy = age != 2;
            var snapshot = legacy ? job with { Destinations = null, TemporaryFolder = null, Storage = null } : job;
            var type = legacy ? BackupType.Full : BackupType.Differential;
            var run = new BackupRun(Guid.NewGuid(), snapshot, job.Connection!, DateTimeOffset.UtcNow.AddDays(-age),
                RunStatus.Succeeded, [], "Synthetic mixed-generation history", CompletedUtc: DateTimeOffset.UtcNow.AddDays(-age),
                Type: type, SqlOutputFolder: legacy ? null : staging, ServiceOutputFolder: legacy ? null : staging);
            string sqlPath = BackupArtifactPaths.SqlPath(run, "TestDatabase");
            string finalPath = Path.Combine(folder, Path.GetFileName(sqlPath));
            byte[] content = Encoding.UTF8.GetBytes("Synthetic mixed backup " + age);
            await File.WriteAllBytesAsync(finalPath, content);
            string hash = Convert.ToHexString(SHA256.HashData(content));
            string firstLsn = age == 30 ? "300" : "100";
            string lastLsn = age == 30 ? "400" : "200";
            DestinationTransfer[]? transfers = legacy ? null : [new DestinationTransfer(destination.Id,
                StageStatus.Succeeded, finalPath, "Synthetic confirmed folder copy", hash, content.Length)];
            var result = new DatabaseBackupResult("TestDatabase", StageStatus.Succeeded, StageStatus.Succeeded,
                sqlPath, BackupSetId: run.Id.ToString(), FirstLsn: firstLsn, LastLsn: lastLsn,
                CheckpointLsn: firstLsn, DatabaseBackupLsn: "100", DatabaseId: "FixtureDatabaseId", RecoveryForkId: "FixtureFork",
                HeaderCopyOnly: false, Sha256: hash, HasSingleRecoveryFork: true, HeaderType: type, DestinationTransfers: transfers);
            run = run with { Databases = [result] };
            await runs.AddAsync(run, null, CancellationToken.None, destinationAccess: legacy ? [] : credentials);
            if (age == 90)
            {
                oldestRunId = run.Id;
            }
        }

        var retention = new RetentionService(jobs, runs, new CleanupAuditStore(configuration), new ConnectionSecrets(configuration),
            new FixtureHistoryInspector(), new StorageSecrets(configuration), configuration, new DestinationStorage());
        var preview = await retention.PreviewAsync(job.Id, CancellationToken.None);
        Check(preview.Decisions.Length == 3 && !preview.Decisions.Single(item => item.Artifact.RunId == oldestRunId).Delete
            && preview.Decisions.Single(item => item.Artifact.RunId == oldestRunId).Reason.Contains("Differential", StringComparison.Ordinal),
            "Retention combines legacy and new destination evidence to protect an older Full required by a new Differential");
    }

    private static BackupDestination AzureDestination()
    {
        return new BackupDestination(Guid.NewGuid(), "Azure", DestinationKind.AzureBlob,
            "https://fictionalaccount.blob.core.windows.net/backups", "sql");
    }

    private static BackupDestination S3Destination()
    {
        return new BackupDestination(Guid.NewGuid(), "S3", DestinationKind.AmazonS3,
            "fictional-sqlbackup-bucket", "sql", "eu-central-1", "FictionalAccessKeyId");
    }

    private static string NewDirectory()
    {
        return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SqlBackup.Destinations", Guid.NewGuid().ToString("N"))).FullName;
    }

    private static void Reject(Action action, string message)
    {
        try
        {
            action();
            throw new InvalidOperationException(message);
        }
        catch (ArgumentException)
        {
            Check(true, message);
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
