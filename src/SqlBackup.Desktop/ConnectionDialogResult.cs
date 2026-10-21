using SqlBackup.Core;

namespace SqlBackup.Desktop;

public sealed record ConnectionDialogResult(
    ConnectionConfiguration Connection,
    string Password,
    ServerInspection Inspection);
