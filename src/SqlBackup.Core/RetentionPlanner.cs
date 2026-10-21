using System.Globalization;

namespace SqlBackup.Core;

public static class RetentionPlanner
{
    public static CleanupDecision[] Plan(RetentionArtifact[] artifacts, int days, DateTimeOffset nowUtc)
    {
        var decisions = new List<CleanupDecision>();
        foreach (var group in artifacts.GroupBy(item => (item.Server.ToUpperInvariant(), item.Database.ToUpperInvariant())))
        {
            var ordered = group.OrderBy(item => item.CreatedUtc).ToArray();
            string? uncertainty = FindUncertainty(ordered);
            var cutoff = nowUtc.AddDays(-days);
            var anchor = ordered.LastOrDefault(item => item.Type == BackupType.Full && !item.CopyOnly
                && item.CreatedUtc <= cutoff);
            foreach (var artifact in ordered)
            {
                string reason = "Retained: recovery-window anchor or newer artifact.";
                bool delete = false;
                if (days == 0)
                {
                    reason = "Retained: keep-all policy.";
                }
                else if (uncertainty is not null)
                {
                    reason = uncertainty;
                }
                else if (anchor is null)
                {
                    reason = "Retained: no verified Full anchor before the recovery window.";
                }
                else if (artifact.CopyOnly)
                {
                    reason = "Retained: independent copy-only recovery point.";
                }
                else if (artifact.Type == BackupType.Full && ordered.Any(item => item.Type == BackupType.Differential
                    && item.CreatedUtc >= anchor.CreatedUtc && item.BaseLsn == artifact.CheckpointLsn))
                {
                    reason = "Retained: Full base required by a retained Differential.";
                }
                else if (artifact.CreatedUtc < anchor.CreatedUtc)
                {
                    delete = true;
                    reason = "Eligible: older generation superseded by a retained verified Full recovery anchor.";
                }

                decisions.Add(new CleanupDecision(artifact, delete, reason));
            }
        }

        return decisions.ToArray();
    }

    private static string? FindUncertainty(RetentionArtifact[] artifacts)
    {
        if (artifacts.Select(item => (item.DatabaseId, item.RecoveryForkId)).Distinct().Count() != 1)
        {
            return "Retained: database identity or recovery fork changed; dependencies need inspection.";
        }

        foreach (var item in artifacts)
        {
            if (!item.Verified || string.IsNullOrWhiteSpace(item.DatabaseId)
                || string.IsNullOrWhiteSpace(item.RecoveryForkId) || string.IsNullOrWhiteSpace(item.BackupSetId)
                || string.IsNullOrWhiteSpace(item.Sha256) || !TryLsn(item.FirstLsn, out _)
                || !TryLsn(item.LastLsn, out _) || !TryLsn(item.CheckpointLsn, out _))
            {
                return "Retained: incomplete identity, integrity or verification metadata.";
            }

            if (item.Type == BackupType.Differential && !artifacts.Any(full => full.Type == BackupType.Full
                && !full.CopyOnly && full.CheckpointLsn == item.BaseLsn && full.CreatedUtc <= item.CreatedUtc))
            {
                return "Retained: Differential base is missing or was created by another tool.";
            }
        }

        // Each regular Full starts an independently recoverable generation. Logs must cover it without a gap.
        var fulls = artifacts.Where(item => item.Type == BackupType.Full && !item.CopyOnly).ToArray();
        foreach (var full in fulls)
        {
            var nextFull = fulls.FirstOrDefault(item => item.CreatedUtc > full.CreatedUtc);
            decimal covered = decimal.Parse(full.LastLsn!, CultureInfo.InvariantCulture);
            foreach (var log in artifacts.Where(item => item.Type == BackupType.Log
                && item.CreatedUtc >= full.CreatedUtc && (nextFull is null || item.CreatedUtc < nextFull.CreatedUtc)))
            {
                decimal first = decimal.Parse(log.FirstLsn!, CultureInfo.InvariantCulture);
                decimal last = decimal.Parse(log.LastLsn!, CultureInfo.InvariantCulture);
                if (first > covered || last <= first)
                {
                    return "Retained: Log gap or external Log backup; complete recovery dependencies are uncertain.";
                }

                covered = Math.Max(covered, last);
            }
        }

        if (artifacts.Any(item => item.Type == BackupType.Log
            && !fulls.Any(full => full.CreatedUtc <= item.CreatedUtc)))
        {
            return "Retained: Log backups have no owned Full recovery base.";
        }

        return null;
    }

    private static bool TryLsn(string? value, out decimal lsn)
    {
        return decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out lsn) && lsn > 0;
    }
}
