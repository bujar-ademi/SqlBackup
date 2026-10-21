using SqlBackup.Setup;
using System.Security.Principal;
using Microsoft.Data.Sqlite;

if (args.Length == 1 && args[0] == "--verify-payload")
{
    // Build-time runtime check only: no machine-wide data, service control or network access.
    using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT sqlite_version();";
    Console.WriteLine("Setup payload verified; bundled SQLite " + command.ExecuteScalar());
    return 0;
}

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: SqlBackup.Setup <prepare|commit|rollback|uninstall-prepare|uninstall|resume> <install-directory> <numeric-version> <result-file>");
    return 2;
}

string result;
try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        throw new ArgumentException("Run the installer/recovery helper as an administrator.");
    }

    var manager = new InstallationManager(new SetupPaths(args[1]));
    switch (args[0])
    {
        case "prepare":
            await manager.PrepareAsync(args[2]);
            break;
        case "commit":
            await manager.CommitAsync(args[2]);
            break;
        case "rollback":
            await manager.RollbackAsync();
            break;
        case "uninstall-prepare":
            await manager.PrepareUninstallAsync();
            break;
        case "uninstall":
            manager.Uninstall();
            break;
        case "resume":
            await manager.ResumeAsync();
            break;
        default:
            throw new ArgumentException("Unknown setup operation.");
    }

    result = "Setup operation completed.";
    File.WriteAllText(args[3], result);
    Console.WriteLine(result);
    return 0;
}
catch (Exception exception)
{
    result = exception is ArgumentException ? exception.Message
        : "Setup operation failed (" + exception.GetType().Name + "). Data is retained. Review the installation and recovery guide in the project's GitHub docs folder; a pending upgrade may require rollback.";
    File.WriteAllText(args[3], result);
    Console.Error.WriteLine(result);
    return 1;
}
