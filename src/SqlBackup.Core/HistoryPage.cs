namespace SqlBackup.Core;

public sealed record HistoryPage(RunSummary[] Items, bool HasMore, int Offset);
