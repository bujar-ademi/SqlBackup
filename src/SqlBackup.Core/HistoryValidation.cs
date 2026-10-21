namespace SqlBackup.Core;

public static class HistoryValidation
{
    public static void Validate(HistoryQuery query)
    {
        if (query.Offset < 0 || query.Offset > 1000000 || query.Limit is < 1 or > 50
            || query.Database?.Length > 128 || (query.Status.HasValue && !Enum.IsDefined(query.Status.Value))
            || (query.Type.HasValue && !Enum.IsDefined(query.Type.Value))
            || (query.FromUtc.HasValue && query.ToUtc.HasValue && query.FromUtc > query.ToUtc))
        {
            throw new ArgumentException("Invalid history filters or page size (maximum 50). Check the date range.");
        }
    }
}
