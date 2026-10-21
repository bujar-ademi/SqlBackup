namespace SqlBackup.Core;

public sealed class JobValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
