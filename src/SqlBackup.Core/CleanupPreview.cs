namespace SqlBackup.Core;

public sealed record CleanupPreview(string Token, CleanupDecision[] Decisions, string Message);
