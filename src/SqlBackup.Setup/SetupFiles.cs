using SqlBackup.Infrastructure;

namespace SqlBackup.Setup;

public static class SetupFiles
{
    public static void CopyTree(string source, string destination)
    {
        ArtifactFiles.EnsureNoLinks(source);
        ArtifactFiles.EnsureNoLinks(destination);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            ArtifactFiles.EnsureNoLinks(file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string child in Directory.EnumerateDirectories(source))
        {
            CopyTree(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }

    public static void DeleteManagedTree(string directory, string parent)
    {
        string resolved = Path.GetFullPath(directory);
        string root = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || resolved.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Refusing to remove a directory outside the managed installation root.");
        }

        ArtifactFiles.EnsureNoLinks(directory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            DeleteManagedTree(child, parent);
        }

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            ArtifactFiles.EnsureNoLinks(file);
            File.Delete(file);
        }

        Directory.Delete(directory);
    }
}
