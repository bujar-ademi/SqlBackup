using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public static class BackupArtifactPaths
{
    public static string SqlPath(BackupRun run, string database)
    {
        var outputJob = run.Job;
        if (run.Job.Destinations is not null)
        {
            outputJob = outputJob with
            {
                Destination = run.SqlOutputFolder ?? throw new IOException("The SQL output snapshot is missing."),
                TemporaryFolder = null,
                Destinations = null,
                Storage = null
            };
        }

        return BackupSql.CreateArtifactPath(outputJob, database, run.Id, run.CreatedUtc, run.Type);
    }
}
