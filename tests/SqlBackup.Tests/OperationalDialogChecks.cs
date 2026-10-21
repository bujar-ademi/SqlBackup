using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlBackup.Core;
using SqlBackup.Desktop;

namespace SqlBackup.Tests;

public static class OperationalDialogChecks
{
    public static void Run(string previewPath, BackupJob job)
    {
        CheckSavingJob(job);
        bool read = false;
        var notification = new NotificationMessage("run/fixture", DateTimeOffset.UtcNow, job.Id, Guid.NewGuid(),
            "Warning", "Backup run Failed", "Backup 1/1 · Verify issues 0 · Copy issues 1. Open execution history for details.");
        var inbox = new NotificationsDialog(request =>
        {
            read = read || request.Operation == "ReadNotification";
            var item = notification with { ReadUtc = read ? DateTimeOffset.UtcNow : null };
            return Task.FromResult(new ServiceResponse(true, "Fixture", Notifications: new NotificationPage([item], read ? 0 : 1)));
        });
        Check(inbox.RefreshAsync().IsCompletedSuccessfully && ((ListBox)inbox.FindName("NotificationList")).Items.Count == 1,
            "Notification inbox receives local service alerts without marking them read automatically");
        ((ListBox)inbox.FindName("NotificationList")).SelectedIndex = 0;
        Check(inbox.MarkSelectedReadAsync().IsCompletedSuccessfully && read,
            "Mark selected as read sends an explicit acknowledgement to the service");
        Render(inbox, Path.ChangeExtension(previewPath, ".notifications.png"), 850, 570);
        inbox.Close();
        var started = DateTimeOffset.UtcNow.AddSeconds(-22);
        var fixtureRun = new BackupRun(notification.RunId!.Value, job, job.Connection!, started,
            RunStatus.Failed, [new DatabaseBackupResult("FixtureDatabase", StageStatus.Succeeded, StageStatus.Failed,
                @"D:\SqlBackups\FixtureDatabase_Full_example.bak", 67108864,
                Message: "Fictional checksum verification failure. No live SQL was used.")],
            "Fictional verification failure. No live SQL was used.", StartedUtc: started, CompletedUtc: DateTimeOffset.UtcNow);
        RunLogEntry[] observed =
        [
            new(started, "Job", "Running", "Starting manual Full backup job. Fictional layout fixture."),
            new(started.AddSeconds(1), "Connection", "Running", "Starting read-only SQL inspection, attempt 1."),
            new(started.AddSeconds(2), "Connection", "Succeeded", "SQL inspection complete · Fictional SQL Server."),
            new(started.AddSeconds(3), "Backup", "Running", "Backup: Running.", "FixtureDatabase"),
            new(started.AddSeconds(18), "Backup", "Succeeded", "Backup: Succeeded. Payload 64 MiB · D:\\SqlBackups\\FixtureDatabase_Full_example.bak", "FixtureDatabase"),
            new(started.AddSeconds(19), "VERIFYONLY", "Running", "VERIFYONLY: Running.", "FixtureDatabase"),
            new(started.AddSeconds(21), "VERIFYONLY", "Failed", "Fictional checksum verification failure.", "FixtureDatabase"),
            new(started.AddSeconds(22), "Job", "Failed", "Backup file was created; verification needs attention.")
        ];
        var details = new OperationDetailsDialog(notification, _ => Task.FromResult(new ServiceResponse(true, "Fixture", Runs: [fixtureRun], Log: observed)));
        Check(details.LoadAsync().IsCompletedSuccessfully
            && ((TextBox)details.FindName("Details")).Text.Contains("VERIFYONLY: Failed"),
            "An alert opens its preserved run evidence with separate stage results");
        ((ComboBox)details.FindName("LogFilter")).SelectedIndex = 1;
        Check(((ListBox)details.FindName("LogRecords")).Items.Count == 2,
            "Job log filtering isolates failed verification and the failed overall outcome");
        ((ComboBox)details.FindName("LogFilter")).SelectedIndex = 0;
        Render(details, Path.ChangeExtension(previewPath, ".operation.png"), 1030, 640);
        details.Close();

        var diagnostics = new DiagnosticsDialog(job.Id, request => Task.FromResult(new ServiceResponse(true,
            "Fixture diagnostics", Diagnostics:
            [
                new DiagnosticItem("SQL connection", StageStatus.Succeeded, "SQL Server fixture. No production connection was used."),
                new DiagnosticItem("SQL destination visibility", StageStatus.Succeeded, "Folder visible. Write access is not proven by visibility."),
                new DiagnosticItem("Secondary folder", StageStatus.Failed, "Service account access could not be confirmed. Inspect share permissions.")
            ])));
        Check(diagnostics.CheckAsync().IsCompletedSuccessfully && ((ListBox)diagnostics.FindName("Checks")).Items.Count == 3,
            "Diagnostics presents separate successful and failed checks instead of a blanket success");
        Render(diagnostics, Path.ChangeExtension(previewPath, ".diagnostics.png"), 880, 580);
        diagnostics.Close();
        var about = new AboutDialog();
        Render(about, Path.ChangeExtension(previewPath, ".about.png"), 600, 510);
        about.Close();
    }

    private static void CheckSavingJob(BackupJob original)
    {
        ServiceRequest? saved = null;
        var window = new MainWindow(request =>
        {
            saved = request;
            return Task.FromResult(new ServiceResponse(true, "Job saved.",
                Jobs: request.Job is null ? [] : [request.Job with { Revision = request.Job.Revision + 1 }]));
        });
        var jobs = (ListBox)window.FindName("JobsList");
        jobs.ItemsSource = new[] { new JobListItem(original) };
        jobs.SelectedIndex = 0;
        ((TextBox)window.FindName("JobName")).Text = "Renamed fixture";
        ((CheckBox)window.FindName("NotifyFailure")).IsChecked = false;
        typeof(MainWindow).GetMethod("SaveJob_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()]);
        Check(saved?.Operation == "SaveJob" && saved.Job?.Name == "Renamed fixture"
            && saved.Job.Notifications?.OnFailure == false
            && ((TextBlock)window.FindName("Result")).Text == "Job saved.",
            "Desktop Save Job sends edited settings and applies the returned saved revision");
        var invalidSchedule = new BackupSchedule(true, "UTC", new DateTime(2026, 10, 21), 0, 0, 0,
            Enum.GetValues<DayOfWeek>(), []);
        jobs.ItemsSource = new[] { new JobListItem(original with { Schedule = invalidSchedule }) };
        jobs.SelectedIndex = 0;
        Check(((TextBlock)window.FindName("ScheduleSummary")).Text.Contains("needs attention"),
            "Invalid saved schedules show a review warning without crashing job selection");
        window.Close();
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
