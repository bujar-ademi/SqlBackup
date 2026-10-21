namespace SqlBackup.Core;

public sealed record CleanupDecision(RetentionArtifact Artifact, bool Delete, string Reason);
