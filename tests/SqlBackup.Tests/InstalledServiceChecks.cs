using System.IO;
using Microsoft.Data.Sqlite;
using SqlBackup.Core;

namespace SqlBackup.Tests;

public static class InstalledServiceChecks
{
    private static readonly Guid FixtureId = Guid.Parse("99e99e01-886d-4190-a00f-4411cb7c74db");

    public static async Task RunAsync(string operation)
    {
        if (Environment.GetEnvironmentVariable("SQLBACKUP_INSTALLER_SMOKE") != "1")
        {
            throw new InvalidOperationException("Installed checks require the explicit clean-machine installer smoke harness.");
        }

        if (operation == "corrupt-offline")
        {
            string database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlBackup", "configuration.db");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Jobs;";
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1)
            {
                throw new InvalidOperationException("Offline mutation requires exactly one synthetic smoke job.");
            }

            command.CommandText = "SELECT COUNT(*) FROM Jobs WHERE Id = $id AND json_extract(Payload, '$.Enabled') = 0;";
            command.Parameters.AddWithValue("$id", FixtureId.ToString());
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1)
            {
                throw new InvalidOperationException("Refusing to mutate a store without the disabled smoke fixture.");
            }

            command.CommandText = "PRAGMA user_version = 999;";
            await command.ExecuteNonQueryAsync();
            return;
        }

        var client = new ServiceClient();
        if (operation == "seed")
        {
            var job = new BackupJob(FixtureId, "Installer smoke fixture", ["FictionalDatabase"], @"\\installer-fixture.invalid\backups",
                Enabled: false, Connection: new ConnectionConfiguration("Installer fixture", "installer-fixture.invalid", false, "fictional-user"),
                Storage: new StoragePolicy(DestinationUserName: @"FICTIONAL\fixture"), TemporaryFolder: @"C:\NeverUsedStaging");
            var response = await client.SendAsync(new ServiceRequest("SaveJob", Job: job, Password: "Fictional-Installer-SQL-Secret",
                StoragePasswords: new StoragePasswords("Fictional-Installer-NAS-Secret")));
            if (!response.Success)
            {
                throw new InvalidOperationException("Installed service could not persist the disabled fictional fixture.");
            }
        }
        else if (operation != "check")
        {
            throw new ArgumentException("Unknown installed-service fixture operation.");
        }

        var loaded = await client.SendAsync(new ServiceRequest("Jobs"));
        var fixture = loaded.Jobs?.SingleOrDefault(job => job.Id == FixtureId);
        if (fixture?.Enabled != false || fixture.Connection?.HasPassword != true
            || fixture.Storage?.DestinationUserName != @"FICTIONAL\fixture")
        {
            throw new InvalidOperationException("Installed fixture or protected credential metadata did not survive.");
        }

        var readiness = await client.SendAsync(new ServiceRequest("SetupReadiness"));
        if (!readiness.Success)
        {
            throw new InvalidOperationException("LocalSystem could not initialize storage/decrypt its saved credentials.");
        }

        Console.WriteLine("PASS: Installed LocalSystem service retains the disabled job and decrypts its SQL/NAS credentials without contacting either server.");
    }
}
