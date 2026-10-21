using SqlBackup.Infrastructure;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SqlBackup.Setup;

public sealed class SetupPaths
{
    public string InstallDirectory { get; }
    public string DataDirectory { get; }
    public string RecordPath => Path.Combine(DataDirectory, "installation.json");
    public string MaintenancePath => Path.Combine(DataDirectory, "setup-maintenance.json");
    public string UninstallStatePath => Path.Combine(DataDirectory, "setup-uninstall.json");
    public string TransactionDirectory => Path.Combine(DataDirectory, "UpgradeBackups", "pending");
    public string TransactionPath => Path.Combine(TransactionDirectory, "transaction.json");
    public static string[] ManagedFolders => ["Desktop", "Service", "Setup", "Docs", "Legal"];

    public SetupPaths(string installDirectory)
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SqlBackup");
        if (!Path.GetFullPath(installDirectory).TrimEnd('\\').Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Setup requires the standard Program Files\\SqlBackup installation directory.");
        }

        InstallDirectory = expected;
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlBackup");
        ArtifactFiles.EnsureNoLinks(InstallDirectory);
        ArtifactFiles.EnsureNoLinks(DataDirectory);
    }

    public void ProtectDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(DataDirectory).SetAccessControl(security);
    }
}

