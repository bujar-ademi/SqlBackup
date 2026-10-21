using SqlBackup.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

namespace SqlBackup.Setup;

public sealed class InstallationManager(SetupPaths paths)
{
    private readonly SetupService service = new(paths);

    public async Task PrepareAsync(string version)
    {
        ValidateVersion(version);
        RejectOpenDesktop();
        var previous = Read<InstallationRecord>(paths.RecordPath);
        bool hadService = service.Exists();
        bool hadDatabase = File.Exists(Path.Combine(paths.DataDirectory, "configuration.db"));
        if ((hadDatabase || hadService) && previous is null)
        {
            throw new ArgumentException("Existing unmanaged SqlBackup data/service was found. Do not change its DPAPI identity. Follow the console-to-service migration guide before installing.");
        }

        if (previous is not null && (previous.ServiceIdentity != "S-1-5-18"
            || !previous.InstallDirectory.Equals(paths.InstallDirectory, StringComparison.OrdinalIgnoreCase)
            || Version.Parse(previous.Version) > Version.Parse(version)))
        {
            throw new ArgumentException("Installation identity/path mismatch or version downgrade. Use the documented recovery procedure.");
        }

        var pending = Read<SetupTransaction>(paths.TransactionPath);
        if (pending?.State is "Prepared" or "Preparing")
        {
            throw new ArgumentException("An unfinished setup transaction exists. Complete its documented rollback before starting another installer.");
        }

        paths.ProtectDataDirectory();
        if (Directory.Exists(paths.TransactionDirectory))
        {
            ArtifactFiles.EnsureNoLinks(paths.TransactionDirectory);
            string archive = Path.Combine(paths.DataDirectory, "UpgradeBackups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ"));
            Directory.Move(paths.TransactionDirectory, archive);
        }

        Directory.CreateDirectory(paths.TransactionDirectory);
        bool wasRunning = service.IsRunning();
        if (wasRunning)
        {
            await service.BeginMaintenanceAsync();
        }

        var transaction = new SetupTransaction(paths.InstallDirectory, version, hadService, wasRunning, hadDatabase, "Preparing");
        Write(paths.TransactionPath, transaction);
        try
        {
            File.WriteAllText(paths.MaintenancePath, "{\"Maintenance\":true}");
            service.Stop();
            using var hostLock = LockStoppedHost();
            foreach (string folder in SetupPaths.ManagedFolders)
            {
                string source = Path.Combine(paths.InstallDirectory, folder);
                if (Directory.Exists(source))
                {
                    SetupFiles.CopyTree(source, Path.Combine(paths.TransactionDirectory, "Files", folder));
                }
            }

            if (previous is not null)
            {
                Write(Path.Combine(paths.TransactionDirectory, "installation.json"), previous);
            }

            if (hadDatabase)
            {
                SnapshotDatabase();
            }

            Write(paths.TransactionPath, transaction with { State = "Prepared" });
        }
        catch
        {
            await RollbackAsync();
            throw;
        }
    }

    public async Task CommitAsync(string version)
    {
        var transaction = RequireTransaction();
        if (transaction.Version != version || transaction.State != "Prepared")
        {
            throw new ArgumentException("Setup transaction does not match the requested version/state.");
        }

        service.Configure();
        await service.StartAndCheckAsync();
        Write(paths.RecordPath, new InstallationRecord(version, paths.InstallDirectory));
        Write(paths.TransactionPath, transaction with { State = "Committed" });
        File.Delete(paths.MaintenancePath);
    }

    public async Task RollbackAsync()
    {
        var transaction = Read<SetupTransaction>(paths.TransactionPath);
        if (transaction is null || transaction.State is "Committed" or "RolledBack")
        {
            return;
        }

        ValidateTransaction(transaction);
        RejectOpenDesktop();
        service.Stop();
        using (LockStoppedHost())
        {
            if (transaction.State == "Prepared")
            {
                foreach (string folder in SetupPaths.ManagedFolders)
                {
                    string destination = Path.Combine(paths.InstallDirectory, folder);
                    SetupFiles.DeleteManagedTree(destination, paths.InstallDirectory);
                    string backup = Path.Combine(paths.TransactionDirectory, "Files", folder);
                    if (Directory.Exists(backup))
                    {
                        SetupFiles.CopyTree(backup, destination);
                    }
                }

                RestoreDatabase(transaction.HadDatabase);
                string record = Path.Combine(paths.TransactionDirectory, "installation.json");
                if (File.Exists(record))
                {
                    File.Copy(record, paths.RecordPath, overwrite: true);
                }
                else
                {
                    File.Delete(paths.RecordPath);
                }
            }
        }

        if (!transaction.HadService)
        {
            service.Remove();
        }

        if (transaction.WasRunning)
        {
            service.Configure();
            await service.StartAndCheckAsync();
        }

        Write(paths.TransactionPath, transaction with { State = "RolledBack" });
        File.Delete(paths.MaintenancePath);
    }

    public async Task PrepareUninstallAsync()
    {
        var pending = Read<SetupTransaction>(paths.TransactionPath);
        if (pending?.State is "Prepared" or "Preparing")
        {
            throw new ArgumentException("Complete rollback of the pending upgrade before uninstalling.");
        }

        RejectOpenDesktop();
        bool wasRunning = service.IsRunning();
        if (wasRunning)
        {
            await service.BeginMaintenanceAsync();
        }

        Write(paths.UninstallStatePath, new UninstallState(wasRunning));
        service.Stop();
        using var hostLock = LockStoppedHost();
    }

    public void Uninstall()
    {
        service.Remove();
        File.Delete(paths.MaintenancePath);
        File.Delete(paths.UninstallStatePath);
        // Configuration, identity record, upgrade backups and all SQL artifacts remain untouched.
    }

    public async Task ResumeAsync()
    {
        var pending = Read<SetupTransaction>(paths.TransactionPath);
        if (pending?.State is "Prepared" or "Preparing")
        {
            throw new ArgumentException("A pending application upgrade must be rolled back before maintenance can be released.");
        }

        var uninstall = Read<UninstallState>(paths.UninstallStatePath);
        if (service.Exists() && uninstall?.WasRunning != false)
        {
            await service.StartAndCheckAsync();
        }

        File.Delete(paths.MaintenancePath);
        File.Delete(paths.UninstallStatePath);
    }

    private void SnapshotDatabase()
    {
        SetupDatabase.Snapshot(Path.Combine(paths.DataDirectory, "configuration.db"),
            Path.Combine(paths.TransactionDirectory, "configuration.db"));
    }

    private void RestoreDatabase(bool hadDatabase)
    {
        string? snapshot = hadDatabase ? Path.Combine(paths.TransactionDirectory, "configuration.db") : null;
        SetupDatabase.Restore(paths.DataDirectory, snapshot);
    }

    private FileStream LockStoppedHost()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(paths.DataDirectory, "service.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // SCM can report Stopped slightly before the process releases its last handle.
                Thread.Sleep(100);
            }
        }
    }

    private SetupTransaction RequireTransaction()
    {
        var transaction = Read<SetupTransaction>(paths.TransactionPath)
            ?? throw new ArgumentException("The setup recovery transaction is missing.");
        ValidateTransaction(transaction);
        return transaction;
    }

    private void ValidateTransaction(SetupTransaction transaction)
    {
        if (!transaction.InstallDirectory.Equals(paths.InstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Recovery transaction belongs to another installation path.");
        }
    }

    private void RejectOpenDesktop()
    {
        foreach (var process in Process.GetProcessesByName("SqlBackup.Desktop"))
        {
            using (process)
            {
                throw new ArgumentException("Close the SqlBackup desktop application before continuing setup.");
            }
        }
    }

    private static void ValidateVersion(string version)
    {
        if (!Version.TryParse(version, out var parsed) || parsed.Major < 0 || parsed.Build < 0)
        {
            throw new ArgumentException("Setup requires a numeric major.minor.patch version.");
        }
    }

    private static T? Read<T>(string path)
    {
        ArtifactFiles.EnsureNoLinks(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default;
    }

    private static void Write<T>(string path, T record)
    {
        ArtifactFiles.EnsureNoLinks(path);
        string temporary = path + ".new";
        ArtifactFiles.EnsureNoLinks(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(record));
        File.Move(temporary, path, overwrite: true);
    }
}
