using System.Security.Cryptography;
using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

public static class ArtifactFiles
{
    public static string Resolve(string folder, string filename)
    {
        JobValidation.ValidateDestination(folder);
        if (Path.GetFileName(filename) != filename || string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException("An artifact must be a direct child of its managed folder.");
        }

        string path = Path.GetFullPath(Path.Combine(folder, filename));
        EnsureNoLinks(path);
        return path;
    }

    public static void EnsureNoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Managed artifacts cannot pass through symbolic links or junctions.");
                }
            }

            current = Path.GetDirectoryName(current);
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        EnsureNoLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
