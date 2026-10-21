namespace SqlBackup.Core;

public sealed record DiagnosticItem(string Name, StageStatus Status, string Message);
