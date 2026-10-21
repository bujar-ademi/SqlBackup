using Microsoft.Data.SqlClient;
using SqlBackup.Core;
using System.Data;

namespace SqlBackup.Infrastructure;

public static class BackupSql
{
    public static string CreateArtifactPath(BackupJob job, string database, Guid runId, DateTimeOffset timestamp, BackupType type = BackupType.Full)
    {
        JobValidation.Validate(job);
        string safeName = SafeName(database);
        int databaseIndex = Array.IndexOf(job.Databases, database);
        if (databaseIndex < 0)
        {
            throw new ArgumentException("The database is not selected by this job.");
        }

        string extension = type == BackupType.Log ? "trn" : "bak";
        // Null denotes the historical format, retained for immutable run/retention evidence.
        string fileName = job.TemporaryFolder is null
            ? $"{safeName}_{type}_{timestamp:yyyyMMddTHHmmssfffZ}_{runId:N}_{databaseIndex}.{extension}"
            : ReadableFileName(job, databaseIndex, timestamp, type, extension);

        return BackupLocations.SqlFolder(job).TrimEnd('\\') + "\\" + fileName;
    }

    private static string ReadableFileName(BackupJob job, int databaseIndex, DateTimeOffset timestamp,
        BackupType type, string extension)
    {
        var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string fileName = string.Empty;
        for (int index = 0; index <= databaseIndex; index++)
        {
            string stem = $"{SafeName(job.Databases[index])}_{type}_{timestamp.UtcDateTime:yyyy-MM-dd_HH-mm-ss-fffffff}Z";
            fileName = $"{stem}.{extension}";
            int sequence = 2;
            while (!allocated.Add(fileName))
            {
                fileName = $"{stem}_{sequence:00}.{extension}";
                sequence++;
            }
        }

        return fileName;
    }

    public static SqlCommand CreateBackupCommand(SqlConnection connection, BackupJob job, string database, string path, BackupType type = BackupType.Full)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        string identifier = QuoteIdentifier(database);
        string checksum = job.Checksum ? "CHECKSUM" : "NO_CHECKSUM";
        string compression = job.Compression ? "COMPRESSION" : "NO_COMPRESSION";
        string copyOnly = type == BackupType.Full && job.CopyOnly ? ", COPY_ONLY" : string.Empty;
        string differential = type == BackupType.Differential ? ", DIFFERENTIAL" : string.Empty;
        string operation = type == BackupType.Log ? "BACKUP LOG" : "BACKUP DATABASE";
        command.CommandText = $"{operation} {identifier} TO DISK = @path WITH NOINIT, NOSKIP, {checksum}, {compression}{copyOnly}{differential}, NAME = @name, STATS = 10;";
        command.Parameters.Add("@path", SqlDbType.NVarChar, 1024).Value = path;
        string mediaName = "AbaSqlBackup " + Path.GetFileNameWithoutExtension(path);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = mediaName[..Math.Min(mediaName.Length, 128)];
        return command;
    }

    private static string SafeName(string database)
    {
        return new string(database.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_').Take(48).ToArray());
    }

    public static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            throw new ArgumentException("A valid SQL database name is required.");
        }

        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }
}
