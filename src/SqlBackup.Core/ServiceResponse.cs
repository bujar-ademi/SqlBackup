namespace SqlBackup.Core;

public sealed record ServiceResponse(
    bool Success,
    string Message,
    ConnectionConfiguration? Configuration = null,
    BackupJob[]? Jobs = null,
    ServerInspection? Inspection = null,
    BackupRun[]? Runs = null,
    Guid? RunId = null,
    CleanupPreview? Cleanup = null,
    CleanupAudit[]? CleanupHistory = null,
    HistoryPage? History = null,
    OperationalSnapshot? Operations = null,
    NotificationPage? Notifications = null,
    DiagnosticItem[]? Diagnostics = null,
    string? ErrorCode = null,
    RunLogEntry[]? Log = null);
