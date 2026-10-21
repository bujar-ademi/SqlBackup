using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlBackup.Core;
using SqlBackup.Desktop;

namespace SqlBackup.Tests;

public static class DesktopChecks
{
    public static void Render(string outputPath)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderWindow(outputPath);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Desktop rendering failed.", failure);
        }
    }

    private static void RenderWindow(string outputPath)
    {
        BackupRun? fixtureRun = null;
        var requests = new List<ServiceRequest>();
        var window = new MainWindow(request =>
        {
            requests.Add(request);
            return Task.FromResult(new ServiceResponse(true, "Fixture response", Runs: fixtureRun is null ? [] : [fixtureRun],
                History: new HistoryPage(fixtureRun is null ? [] : [RunPresentation.Summarize(fixtureRun)], false, 0)));
        });
        var first = new BackupJob(Guid.NewGuid(), "Application databases", ["FixtureDatabase"], @"D:\SqlBackups", Revision: 1,
            Connection: new ConnectionConfiguration("Application SQL", "FICTIONAL-SQL-01", true, null));
        var second = first with
        {
            Id = Guid.NewGuid(),
            Name = "Reporting database",
            Destination = @"\\nas\sql-backups",
            Connection = new ConnectionConfiguration("Reporting SQL", "FICTIONAL-SQL-02", false, "report-user", HasPassword: true)
        };
        var jobs = (ListBox)window.FindName("JobsList");
        var firstRow = new JobListItem(first);
        var secondRow = new JobListItem(second);
        jobs.ItemsSource = new[] { firstRow, secondRow };
        jobs.SelectedItem = firstRow;
        if (((TextBox)window.FindName("JobName")).Text != first.Name
            || ((TextBlock)window.FindName("DatabaseSummary")).Text != "FixtureDatabase")
        {
            throw new InvalidOperationException("Selecting a job must populate its editor and preserve its saved databases.");
        }

        jobs.SelectedItem = secondRow;
        if (!((TextBlock)window.FindName("ConnectionSummary")).Text.StartsWith("FICTIONAL-SQL-02", StringComparison.Ordinal)
            || window.FindName("Password") is not null)
        {
            throw new InvalidOperationException("Changing jobs must load its own server and never reveal a saved password.");
        }

        jobs.SelectedItem = firstRow;
        ((CheckBox)window.FindName("JobEnabled")).IsChecked = false;
        if (!((TextBlock)window.FindName("ScheduleSummary")).Text.Contains("Job disabled"))
        {
            throw new InvalidOperationException("A disabled job must not advertise an active next run.");
        }

        ((CheckBox)window.FindName("JobEnabled")).IsChecked = true;

        ((FrameworkElement)window.FindName("JobEditor")).IsEnabled = true;
        ((Button)window.FindName("NewJobButton")).IsEnabled = true;
        var history = (ListBox)window.FindName("HistoryList");
        var result = new DatabaseBackupResult("FixtureDatabase", StageStatus.Succeeded, StageStatus.Succeeded,
            @"D:\SqlBackups\FixtureDatabase_Full_example.bak", 2097152,
            Message: "Layout fixture: backup and VERIFYONLY completed. Restore not tested.");
        var run = new BackupRun(Guid.NewGuid(), first,
            first.Connection!,
            DateTimeOffset.UtcNow, RunStatus.Succeeded, [result], "Sample history for layout inspection.",
            DateTimeOffset.UtcNow.AddSeconds(-8), DateTimeOffset.UtcNow);
        fixtureRun = run;
        history.ItemsSource = new[] { RunPresentation.Summarize(run) };
        history.SelectedIndex = 0;
        if (requests.Any(request => request.Operation == "RunDetails"))
        {
            throw new InvalidOperationException("Selecting a compact history row must not fetch full details before its log is opened.");
        }

        ((ComboBox)window.FindName("HistoryStatus")).SelectedItem = RunStatus.Failed;
        ((ComboBox)window.FindName("HistoryType")).SelectedItem = BackupType.Log;
        ((TextBox)window.FindName("HistoryDatabase")).Text = "FixtureDatabase";
        var filter = window.ReadHistoryFilters();
        if (filter.JobId != first.Id || filter.Status != RunStatus.Failed || filter.Type != BackupType.Log
            || filter.Database != "FixtureDatabase")
        {
            throw new InvalidOperationException("History controls must send the selected job, state, type and database.");
        }

        ((ComboBox)window.FindName("HistoryStatus")).SelectedIndex = 0;
        ((ComboBox)window.FindName("HistoryType")).SelectedIndex = 0;
        ((TextBox)window.FindName("HistoryDatabase")).Text = string.Empty;
        firstRow.Update(new JobOverview(first.Id, RunStatus.Failed, run.CreatedUtc, "Failed · copy needs attention", null, "Automatic backups disabled"));
        secondRow.Update(new JobOverview(second.Id, null, null, "No executions yet", null, "Automatic backups disabled"));
        if (firstRow.StatusColor != "#FF9696" || secondRow.StatusColor == "#61C69B")
        {
            throw new InvalidOperationException("Enabled jobs cannot appear healthy without execution evidence.");
        }

        var projection = OperationalProjection.Create([first, second], [run], first.Id, DateTimeOffset.UtcNow);
        ((DataGrid)window.FindName("DatabaseHealth")).ItemsSource = projection.Databases;
        ((Button)window.FindName("NotificationsButton")).Content = "Notifications (1)";
        string originalName = ((TextBox)window.FindName("JobName")).Text;
        typeof(MainWindow).GetMethod("MarkServiceUnavailable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
        if (((FrameworkElement)window.FindName("JobEditor")).IsEnabled
            || ((TextBox)window.FindName("JobName")).Text != originalName || !firstRow.StatusText.Contains("stale"))
        {
            throw new InvalidOperationException("A service outage must mark stale status and preserve the job draft.");
        }

        firstRow.Update(projection.Jobs.Single(item => item.JobId == first.Id));
        secondRow.Update(projection.Jobs.Single(item => item.JobId == second.Id));
        ((TextBlock)window.FindName("HistoryPageLabel")).Text = "1–1";
        ((FrameworkElement)window.FindName("JobEditor")).IsEnabled = true;
        ((Button)window.FindName("NewJobButton")).IsEnabled = true;
        ((TextBlock)window.FindName("ServiceStatus")).Text = "Layout fixture — no live service connection";

        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1380, 810));
        root.Arrange(new Rect(0, 0, 1380, 810));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1380, 810, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, 1380, 810));
        }

        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var file = File.Create(outputPath);
        encoder.Save(file);
        window.Close();
        DialogChecks.Run(outputPath, first, second);
        OperationalDialogChecks.Run(outputPath, first);
        DialogResponsivenessChecks.Run();
        Console.WriteLine("PASS: WPF job editor selection, database preservation, and headless layout render");
        Console.WriteLine(Path.GetFullPath(outputPath));
    }
}
