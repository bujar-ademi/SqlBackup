using System.IO;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

namespace SqlBackup.Tests;

public static class StorageChecks
{
    public static async Task RunAsync()
    {
        CheckPlanner();
        await CheckFilesAsync();
    }

    private static void CheckPlanner()
    {
        var now = DateTimeOffset.UtcNow;
        var oldFull = Artifact(BackupType.Full, now.AddDays(-30), "100", "110", "100", "100");
        var full = Artifact(BackupType.Full, now.AddDays(-10), "200", "210", "200", "200");
        var diff = Artifact(BackupType.Differential, now.AddDays(-3), "200", "240", "230", "200");
        var log = Artifact(BackupType.Log, now.AddDays(-2), "200", "250", "240", "200");
        var plan = RetentionPlanner.Plan([oldFull, full, diff, log], 7, now);
        Check(plan.Single(item => item.Artifact == oldFull).Delete
            && plan.Where(item => item.Artifact != oldFull).All(item => !item.Delete),
            "Retention keeps the Full base and its Differential/Log files while releasing an older generation");
        Check(RetentionPlanner.Plan([oldFull, full, diff with { BaseLsn = "999" }, log], 7, now).All(item => !item.Delete),
            "An external Differential base blocks deletion");
        Check(RetentionPlanner.Plan([oldFull, full, log with { FirstLsn = "220" }], 7, now).All(item => !item.Delete),
            "A missing/external Log segment blocks deletion");
        Check(RetentionPlanner.Plan([oldFull, full with { Sha256 = null }], 7, now).All(item => !item.Delete),
            "Missing integrity evidence blocks deletion");
        Check(RetentionPlanner.Plan([oldFull, full], 0, now).All(item => !item.Delete), "Zero-day policy means keep all");
        var lateDiff = diff with { BaseLsn = "100" };
        Check(!RetentionPlanner.Plan([oldFull, full, lateDiff], 7, now).Single(item => item.Artifact == oldFull).Delete,
            "A retained Differential protects its earlier Full even across a later Full generation");
    }

    private static RetentionArtifact Artifact(BackupType type, DateTimeOffset created, string first,
        string last, string checkpoint, string baseLsn)
    {
        return new RetentionArtifact(Guid.NewGuid(), "Fixture", @"C:\Fixture", @"C:\Fixture\owned.bak",
            type, created, false, "identity", "fork", Guid.NewGuid().ToString(), first, last, checkpoint,
            baseLsn, "FictionalDigest", true, "fixture");
    }

    private static async Task CheckFilesAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SqlBackup.StorageTests", Guid.NewGuid().ToString("N"));
        string primary = Path.Combine(directory, "primary");
        string secondary = Path.Combine(directory, "secondary");
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(secondary);
        var configuration = new ConfigurationStore(Path.Combine(directory, "state"));
        await configuration.InitializeAsync(CancellationToken.None);
        var jobs = new JobStore(configuration);
        var runs = new RunStore(configuration);
        var audit = new CleanupAuditStore(configuration);
        var history = new FixtureHistoryInspector();
        var retention = new RetentionService(jobs, runs, audit, new ConnectionSecrets(configuration), history);
        var job = new BackupJob(Guid.NewGuid(), "Storage fixture", ["Fixture"], primary, CopyOnly: false,
            Connection: new ConnectionConfiguration("Fixture", "fictional-server", true, null),
            Storage: new StoragePolicy(7, secondary, 0));
        await jobs.SaveAsync(job, CancellationToken.None);
        job = await jobs.GetAsync(job.Id, CancellationToken.None);
        var old = await SaveArtifactAsync(runs, job, DateTimeOffset.UtcNow.AddDays(-30), "100", "110");
        var anchor = await SaveArtifactAsync(runs, job, DateTimeOffset.UtcNow.AddDays(-10), "200", "210");
        var preview = await retention.PreviewAsync(job.Id, CancellationToken.None);
        Check(preview.Decisions.Count(item => item.Delete) == 1
            && preview.Decisions.Where(item => item.Artifact.Folder == secondary).All(item => !item.Delete),
            "Primary and secondary retention are independent; secondary keep-all preserves its copy");
        history.HasExternal = true;
        Check((await retention.PreviewAsync(job.Id, CancellationToken.None)).Decisions.All(item => !item.Delete),
            "External SQL history blocks cleanup even when owned LSN metadata appears complete");
        history.HasExternal = false;
        await File.AppendAllTextAsync(old.Databases[0].ArtifactPath!, "changed");
        await ExpectAsync(() => retention.ExecuteAsync(job.Id, preview.Token, CancellationToken.None),
            "A changed file invalidates the reviewed cleanup token before deletion");
        await File.WriteAllTextAsync(old.Databases[0].ArtifactPath!, "Fixture " + old.Id);
        preview = await retention.PreviewAsync(job.Id, CancellationToken.None);
        await retention.ExecuteAsync(job.Id, preview.Token, CancellationToken.None);
        Check(!File.Exists(old.Databases[0].ArtifactPath) && File.Exists(anchor.Databases[0].ArtifactPath)
            && File.Exists(old.Databases[0].Copies![0].Path),
            "Reviewed cleanup deletes only the old primary; its secondary and retained Full survive");
        Check((await audit.ListAsync(job.Id, CancellationToken.None)).Any(item => item.Outcome == "DeleteIntent")
            && (await audit.ListAsync(job.Id, CancellationToken.None)).Any(item => item.Outcome == "Deleted"),
            "Deletion intent, outcome and retention decisions are durably audited");
        Check((await retention.PreviewAsync(job.Id, CancellationToken.None)).Decisions.All(item => !item.Delete),
            "Audited deletion is not confused with a temporarily unavailable file");

        string source = anchor.Databases[0].ArtifactPath!;
        var sourceHash = anchor.Databases[0].Sha256!;
        try
        {
            await new ArtifactTransfer().CopyAsync(source, secondary, sourceHash, CancellationToken.None);
            throw new InvalidOperationException("Expected existing-target rejection.");
        }
        catch (IOException)
        {
            Check(await ArtifactFiles.HashAsync(anchor.Databases[0].Copies![0].Path, CancellationToken.None) == sourceHash,
                "Copy collision preserves the existing completed artifact without overwrite");
        }

        string mismatchFolder = Path.Combine(directory, "mismatch");
        Directory.CreateDirectory(mismatchFolder);
        try
        {
            await new ArtifactTransfer().CopyAsync(source, mismatchFolder, "MismatchedDigest", CancellationToken.None);
            throw new InvalidOperationException("Expected hash mismatch rejection.");
        }
        catch (IOException)
        {
            Check(!File.Exists(Path.Combine(mismatchFolder, Path.GetFileName(source))),
                "Integrity mismatch leaves only partial output, never a completed copy");
        }

        await ExecutionGate.Instance.WaitAsync();
        try
        {
            await ExpectAsync(() => retention.PreviewAsync(job.Id, CancellationToken.None),
                "Cleanup is excluded while backup or transfer stages own the execution gate");
        }
        finally
        {
            ExecutionGate.Instance.Release();
        }

        string badFolder = Path.Combine(directory, "unavailable", "share");
        var failure = await new ArtifactTransfer().ProcessAsync(job with { Storage = new StoragePolicy(0, badFolder) },
            anchor.Databases[0], CancellationToken.None);
        Check(failure.Backup == StageStatus.Succeeded && failure.Copies?.Single().Status == StageStatus.Failed,
            "An unavailable destination fails the copy while preserving primary completion");
        string other = Path.Combine(directory, "interrupted");
        Directory.CreateDirectory(other);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await new ArtifactTransfer().CopyAsync(source, other, anchor.Databases[0].Sha256!, canceled.Token);
            throw new InvalidOperationException("Expected copy cancellation.");
        }
        catch (OperationCanceledException)
        {
            Check(!File.Exists(Path.Combine(other, Path.GetFileName(source))),
                "Interrupted copy never appears as a completed destination artifact");
        }

        await File.WriteAllTextAsync(Path.Combine(other, "unowned.partial"), "Interrupted fixture");
        Check(Directory.GetFiles(other, "*.partial").Length > 0,
            "Partial outputs remain distinct for operator inspection");
    }

    private static async Task<BackupRun> SaveArtifactAsync(RunStore runs, BackupJob job,
        DateTimeOffset created, string first, string last)
    {
        Guid id = Guid.NewGuid();
        string path = BackupSql.CreateArtifactPath(job, "Fixture", id, created);
        await File.WriteAllTextAsync(path, "Fixture " + id);
        var result = new DatabaseBackupResult("Fixture", StageStatus.Succeeded, StageStatus.Succeeded, path,
            BackupSetId: Guid.NewGuid().ToString(), FirstLsn: first, LastLsn: last, CheckpointLsn: first,
            DatabaseBackupLsn: first, DatabaseId: "identity", RecoveryForkId: "fork", HeaderCopyOnly: false, HasSingleRecoveryFork: true, HeaderType: BackupType.Full);
        result = await new ArtifactTransfer().ProcessAsync(job, result, CancellationToken.None);
        Check(result.Copies?.Single().Status == StageStatus.Succeeded
            && result.Copies[0].Sha256 == result.Sha256, "Staged copy is promoted only after SHA-256 matches");
        var run = new BackupRun(id, job, job.Connection!, created, RunStatus.Succeeded, [result], "Fixture complete");
        await runs.AddAsync(run, null, CancellationToken.None);
        return run;
    }

    private static async Task ExpectAsync(Func<Task> action, string message)
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
