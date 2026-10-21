using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlBackup.Core;
using SqlBackup.Desktop;

namespace SqlBackup.Tests;

public static class DialogChecks
{
    public static void Run(string previewPath, BackupJob first, BackupJob second)
    {
        var inspection = new ServerInspection("17.0.fixture", "Fictional SQL Server", true, "Fixture service identity",
        [
            new DatabaseInfo("FixtureDatabase", "ONLINE", "FULL", true, "Ready"),
            new DatabaseInfo("Reporting", "ONLINE", "SIMPLE", true, "Ready"),
            new DatabaseInfo("master", "ONLINE", "SIMPLE", true, "Ready"),
            new DatabaseInfo("OfflineDatabase", "OFFLINE", "FULL", false, "Offline")
        ]);
        ServiceRequest? request = null;
        var dialog = new ConnectionDialog(second, string.Empty, submitted =>
        {
            request = submitted;
            return Task.FromResult(new ServiceResponse(true, "Connected", Inspection: inspection));
        });
        Check(((PasswordBox)dialog.FindName("Password")).Password.Length == 0,
            "Connection dialog never reveals a saved password");
        var test = dialog.TestConnectionAsync();
        Check(test.IsCompletedSuccessfully && request?.Job?.Id == second.Id
            && request.Job.Connection?.Server == second.Connection?.Server,
            "Test Connection sends the selected job's connection through the service");
        Check(dialog.Result is null && second.Connection?.Server == "FICTIONAL-SQL-02",
            "Testing a dialog does not apply or persist job changes");
        Render(dialog, Path.ChangeExtension(previewPath, ".connection.png"), 590, 640);
        ((TextBox)dialog.FindName("ServerName")).Text = "UNSAVED-DIALOG-SERVER";
        Check(!dialog.ReadConfiguration().HasPassword && second.Connection?.Server == "FICTIONAL-SQL-02",
            "Changing dialog fields cannot mutate the original job or borrow its password for a new server");
        dialog.Close();

        string[] originalSelection = ["FixtureDatabase", "master"];
        var databases = new DatabaseSelectionDialog(inspection, first.Connection!.Server, originalSelection);
        ((CheckBox)databases.FindName("ShowSystem")).IsChecked = false;
        Check(((ListBox)databases.FindName("DatabaseList")).Items.Cast<DatabaseSelectionItem>().All(item => !item.IsSystem)
            && databases.Items.Single(item => item.IsSystem).IsSelected,
            "Hiding system databases preserves an existing system-database selection");
        databases.Items.Single(item => item.Database.Name == "Reporting").IsSelected = true;
        Check(originalSelection.Length == 2 && databases.SelectedDatabases.Length == 2,
            "Checkbox edits remain isolated until Save & Close applies the selection");
        Check(databases.TryApplySelection() && databases.SelectedDatabases.Contains("Reporting"),
            "Database Save & Close applies the explicit checkbox selection");
        foreach (var item in databases.Items)
        {
            item.IsSelected = false;
        }

        Check(!databases.TryApplySelection(), "Empty database selections are rejected");
        databases.Items.Single(item => item.Database.Name == "FixtureDatabase").IsSelected = true;
        ((TextBlock)databases.FindName("Status")).Text = "Select the databases to include in this job's backups.";
        Render(databases, Path.ChangeExtension(previewPath, ".databases.png"), 530, 550);
        databases.Close();

        var destination = new DestinationDialog(first, "FictionalDraftPassword", submitted =>
        {
            request = submitted;
            return Task.FromResult(new ServiceResponse(true,
                "SQL Server sees folder: True. Check share permissions for the SQL Server service account."));
        });
        ((TextBox)destination.FindName("FolderPath")).Text = @"\\fictional-nas\sql-backups";
        var destinationTest = destination.TestDestinationAsync();
        Check(destinationTest.IsCompletedSuccessfully && request?.Operation == "CheckStorage"
            && request.Job?.Id == first.Id && request.Job.Destination == @"\\fictional-nas\sql-backups"
            && request.Job.TemporaryFolder == BackupLocations.DefaultTemporaryFolder
            && first.Destination == @"D:\SqlBackups" && destination.SelectedDestination is null,
            "Destination Test uses the job's draft folder and does not apply or persist it");
        Check(destination.TryApplyDestination() && destination.SelectedDestination == @"\\fictional-nas\sql-backups",
            "Destination Save & Close accepts a valid UNC folder");
        ((TextBox)destination.FindName("TemporaryFolder")).Text = @"C:\AbaSqlBackup\Staging";
        ((TextBox)destination.FindName("DestinationUser")).Text = @"fictional-nas\fixture";
        ((PasswordBox)destination.FindName("DestinationPassword")).Password = "FictionalNasPassword";
        Check(destination.TryApplyDestination() && destination.SelectedTemporaryFolder == @"C:\AbaSqlBackup\Staging"
            && destination.SelectedStorage?.DestinationUserName == @"fictional-nas\fixture"
            && destination.SelectedPasswords.Destination == "FictionalNasPassword",
            "Temporary folder and per-destination network credentials are independent job drafts");
        ((TextBox)destination.FindName("PrimaryDays")).Text = "14";
        ((TextBox)destination.FindName("SecondaryFolder")).Text = @"\\fictional-nas\secondary-backups";
        Check(destination.TryApplyDestination() && destination.SelectedStorage?.PrimaryRetentionDays == 14
            && first.Storage is null, "Storage settings remain job-owned drafts until Save Job");
        Render(destination, Path.ChangeExtension(previewPath, ".destination.png"), 620, 710);
        ((TextBox)destination.FindName("FolderPath")).Text = "relative-folder";
        Check(!destination.TryApplyDestination(), "Destination dialog rejects relative paths");
        destination.Close();

        var unavailable = new DestinationDialog(first, string.Empty, submitted =>
            Task.FromResult(new ServiceResponse(false, "Temporary folder is missing or inaccessible.")));
        var rejected = unavailable.CheckAndApplyDestinationAsync();
        Check(rejected.IsCompletedSuccessfully && unavailable.SelectedDestination is null
            && ((TextBlock)unavailable.FindName("TemporaryFolderStatus")).Text.Contains("missing"),
            "An unavailable SQL temporary folder prevents applying the draft and shows an actionable warning");
        unavailable.Close();

        var pendingCheck = new TaskCompletionSource<ServiceResponse>();
        var pendingDestination = new DestinationDialog(first, string.Empty, submitted =>
        {
            request = submitted;
            return pendingCheck.Task;
        });
        ((TextBox)pendingDestination.FindName("TemporaryFolder")).Text = @"%ProgramData%\SqlBackup\Temp";
        var pendingSave = pendingDestination.CheckAndApplyDestinationAsync();
        Check(!pendingSave.IsCompleted && ((Button)pendingDestination.FindName("CancelButton")).IsEnabled
            && request?.Job?.TemporaryFolder == BackupLocations.DefaultTemporaryFolder,
            "Temporary-folder checks keep Cancel available and normalize ProgramData before SQL inspection");
        pendingDestination.Close();
        pendingCheck.SetResult(new ServiceResponse(true, "Visible"));
        Check(pendingSave.IsCompletedSuccessfully && pendingDestination.SelectedDestination is null,
            "A late folder-check response cannot apply a cancelled destination draft");

        var original = new BackupSchedule(true, "Central European Standard Time", new DateTime(2026, 10, 21, 23, 55, 0),
            10080, 1440, 15, Enum.GetValues<DayOfWeek>(), []);
        var schedule = new ScheduleDialog(original);
        Check(schedule.RefreshPreview() && schedule.ReadSchedule().FullIntervalMinutes == 10080,
            "Advanced Schedule renders the same intervals and preview used by the service");
        ((TextBox)schedule.FindName("FullHours")).Text = "24";
        Check(schedule.ReadSchedule().FullIntervalMinutes == 1440 && original.FullIntervalMinutes == 10080
            && schedule.SelectedSchedule is null, "Schedule edits cannot mutate the saved job before application");
        Render(schedule, Path.ChangeExtension(previewPath, ".schedule.png"), 850, 690);
        ((TextBox)schedule.FindName("FullHours")).Text = "0";
        Check(!schedule.RefreshPreview(), "Advanced Schedule rejects a zero Full interval");
        schedule.Close();

        var cleanup = new CleanupDialog(first, submitted => Task.FromResult(new ServiceResponse(true, "Fixture",
            Cleanup: new CleanupPreview("fixture", [], "No eligible files in this isolated preview."), CleanupHistory: [])));
        Check(cleanup.RefreshAsync().IsCompletedSuccessfully
            && !((Button)cleanup.FindName("DeleteButton")).IsEnabled,
            "Cleanup dialog requires reviewed eligible files before enabling deletion");
        Render(cleanup, Path.ChangeExtension(previewPath, ".cleanup.png"), 1000, 660);
        cleanup.Close();
    }

    private static void Render(Window window, string path, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        Console.WriteLine("PASS: " + message);
    }
}

