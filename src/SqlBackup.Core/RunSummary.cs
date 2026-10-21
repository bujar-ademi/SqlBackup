namespace SqlBackup.Core;

public sealed record RunSummary(Guid Id, Guid JobId, string JobName, DateTimeOffset CreatedUtc,
    RunStatus Status, BackupType Type, string Stages, int BackupObjectCount = 0, long? SizeBytes = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string LocalTimeLabel => CreatedUtc.ToLocalTime().ToString("MMM dd HH:mm");
    public string SizeText => DisplaySize.Format(SizeBytes);
    public string TypeText => $"{BackupObjectCount} " + (Type == BackupType.Differential ? "Diff" : Type.ToString());
    public string StatusSymbol => Status switch
    {
        RunStatus.Succeeded => "✓",
        RunStatus.Failed or RunStatus.Interrupted => "!",
        _ => "…"
    };
    public string StatusColor => Status switch
    {
        RunStatus.Succeeded => "#278359",
        RunStatus.Failed or RunStatus.Interrupted => "#CE6244",
        _ => "#4689BF"
    };
}
