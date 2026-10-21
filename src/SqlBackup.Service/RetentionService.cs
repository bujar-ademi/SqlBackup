using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Service;

public sealed class RetentionService(JobStore jobs, RunStore runs, CleanupAuditStore audit,
    ConnectionSecrets secrets, IBackupHistoryInspector historyInspector, StorageSecrets? storageSecrets = null)
{
    public Task<CleanupAudit[]> HistoryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return audit.ListAsync(jobId, cancellationToken);
    }

    public async Task<CleanupPreview> PreviewAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!await ExecutionGate.Instance.WaitAsync(0, cancellationToken))
        {
            throw new ArgumentException("A backup or copy is active. Try cleanup preview after it finishes.");
        }

        try
        {
            return await BuildPreviewAsync(jobId, cancellationToken);
        }
        finally
        {
            ExecutionGate.Instance.Release();
        }
    }

    public async Task<CleanupAudit[]> ExecuteAsync(Guid jobId, string token, CancellationToken cancellationToken)
    {
        if (!await ExecutionGate.Instance.WaitAsync(0, cancellationToken))
        {
            throw new ArgumentException("A backup or copy is active. Refresh the preview after it finishes.");
        }

        try
        {
            var preview = await BuildPreviewAsync(jobId, cancellationToken);
            if (preview.Token != token)
            {
                throw new ArgumentException("Cleanup evidence or policy changed. Refresh and review the preview.");
            }

            foreach (var decision in preview.Decisions.OrderBy(item => item.Artifact.Type == BackupType.Full ? 1 : 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = new CleanupAudit(Guid.NewGuid(), DateTimeOffset.UtcNow, jobId,
                    decision.Artifact.Path, decision.Delete ? "DeleteIntent" : "Retained", decision.Reason);
                // Persist intent BEFORE deletion: an interrupted intent remains visible and blocks future assumptions.
                await audit.AddAsync(entry, cancellationToken);
                if (!decision.Delete)
                {
                    continue;
                }

                string outcome = "Deleted";
                string reason = decision.Reason;
                try
                {
                    var access = storageSecrets is null ? new StorageAccessSnapshot()
                        : await storageSecrets.ReadRunAsync(decision.Artifact.RunId, cancellationToken);
                    var historicalRun = await runs.GetAsync(decision.Artifact.RunId, cancellationToken);
                    StorageSecrets.ValidateSnapshot(historicalRun.Job, access);
                    var credential = string.Equals(decision.Artifact.Folder, historicalRun.Job.Storage?.SecondaryFolder,
                        StringComparison.OrdinalIgnoreCase) ? access.Secondary
                        : historicalRun.Job.TemporaryFolder is null ? access.Source : access.Destination;
                    await ShareAccess.RunAsync(credential, async () =>
                    {
                        await ArtifactDeletion.DeleteVerifiedAsync(decision.Artifact.Path,
                            decision.Artifact.Sha256!, cancellationToken);
                        return true;
                    });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    outcome = "Failed";
                    reason = "Deletion could not be confirmed. Check access, availability and content changes.";
                }

                await audit.AddAsync(entry with
                {
                    Id = Guid.NewGuid(),
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Outcome = outcome,
                    Reason = reason
                }, CancellationToken.None);
                if (outcome != "Deleted")
                {
                    break;
                }
            }

            return await audit.ListAsync(jobId, cancellationToken);
        }
        finally
        {
            ExecutionGate.Instance.Release();
        }
    }

    private async Task<CleanupPreview> BuildPreviewAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        JobValidation.Validate(job);
        var policy = job.Storage ?? new StoragePolicy();
        var history = await runs.ListAsync(cancellationToken, all: true);
        if (history.Any(run => run.Status is RunStatus.Queued or RunStatus.Running))
        {
            throw new ArgumentException("Queued or running work exists. Wait for it before cleanup.");
        }

        var previous = await audit.ListAsync(jobId, cancellationToken, all: true);
        var deleted = previous.Where(item => item.Outcome == "Deleted").Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<CleanupDecision>();
        string primaryFolder = BackupLocations.PrimaryFolder(job);
        var evidenceReader = new RetentionEvidenceReader(secrets, historyInspector, storageSecrets);
        decisions.AddRange(await evidenceReader.ReadAsync(job, primaryFolder, policy.PrimaryRetentionDays,
            false, history, deleted, cancellationToken));
        if (!string.IsNullOrWhiteSpace(policy.SecondaryFolder))
        {
            decisions.AddRange(await evidenceReader.ReadAsync(job, policy.SecondaryFolder, policy.SecondaryRetentionDays,
                true, history, deleted, cancellationToken));
        }

        if (decisions.Count > 500)
        {
            throw new ArgumentException("This preview exceeds 500 artifacts. Cleanup is blocked; use a reviewed maintenance procedure for this larger history.");
        }

        string evidence = JsonSerializer.Serialize(new { job.Revision, Decisions = decisions });
        string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        return new CleanupPreview(token, decisions.ToArray(),
            "Preview only. Dependencies can retain older files. Only reviewed eligible files can be deleted.");

    }
}
