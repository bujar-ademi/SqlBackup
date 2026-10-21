namespace SqlBackup.Core;

public sealed record RunLogEntry(DateTimeOffset? CreatedUtc, string Stage, string Status,
    string Message, string? Database = null)
{
    public string TimeLabel => CreatedUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
    public string Symbol => Status switch
    {
        "Succeeded" => "✓",
        "Failed" or "Unknown" or "Interrupted" => "!",
        "Running" or "Queued" => "…",
        _ => "·"
    };
    public string Color => Status switch
    {
        "Succeeded" => "#278359",
        "Failed" or "Unknown" or "Interrupted" => "#CE6244",
        _ => "#607D91"
    };
}
