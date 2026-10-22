using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class OperationDetailsDialog : Window
{
    private readonly Guid? runId;
    private readonly Guid jobId;
    private readonly Func<ServiceRequest, Task<ServiceResponse>> sendAsync;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private RunLogEntry[] entries = [];
    private BackupRun? currentRun;
    private bool busy;

    public OperationDetailsDialog(NotificationMessage notification, Func<ServiceRequest, Task<ServiceResponse>> sendAsync)
        : this(notification.RunId, notification.JobId, sendAsync)
    {
    }

    public OperationDetailsDialog(Guid runId, Func<ServiceRequest, Task<ServiceResponse>> sendAsync)
        : this(runId, Guid.Empty, sendAsync)
    {
    }

    private OperationDetailsDialog(Guid? runId, Guid jobId, Func<ServiceRequest, Task<ServiceResponse>> sendAsync)
    {
        InitializeComponent();
        this.runId = runId;
        this.jobId = jobId;
        this.sendAsync = sendAsync;
        refreshTimer.Tick += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    public async Task LoadAsync()
    {
        if (busy || lifetime.IsCancellationRequested)
        {
            return;
        }

        busy = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var request = runId.HasValue ? new ServiceRequest("RunDetails", RunId: runId)
                : new ServiceRequest("CleanupHistory", JobId: jobId);
            var response = await sendAsync(request).WaitAsync(lifetime.Token);
            if (response.Runs?.FirstOrDefault() is { } run)
            {
                ApplyRun(run, response.Log);
            }
            else
            {
                RunOverview.Visibility = Visibility.Collapsed;
                LogRecords.Visibility = Visibility.Collapsed;
                Details.Visibility = Visibility.Visible;
                JobLogTitle.Text = "Cleanup audit";
                Details.Text = string.Join("\n\n", (response.CleanupHistory ?? []).Select(item =>
                    $"{item.CreatedUtc:u} · {item.Outcome}\n{item.Path}\n{item.Reason}"));
                if (Details.Text.Length == 0)
                {
                    Details.Text = "No operation evidence is available.";
                }

                RunState.Text = "Recorded cleanup decisions";
                ExportButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Closing the log leaves the service-owned backup running.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EvidenceNote.Text = "Operation evidence could not be loaded. Reconnect and refresh.";
        }
        finally
        {
            busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyRun(BackupRun run, RunLogEntry[]? observed)
    {
        currentRun = run;
        RetryButton.Visibility = run.Job.Destinations is not null && run.Status is RunStatus.Failed or RunStatus.Interrupted
            ? Visibility.Visible : Visibility.Collapsed;
        Details.Text = RunDetailsFormatter.Format(run);
        JobLogTitle.Text = "Log of " + run.Job.Name;
        var summary = RunPresentation.Summarize(run);
        JobStatus.Text = summary.StatusSymbol + " " + run.Status;
        JobStatus.Foreground = (Brush)new BrushConverter().ConvertFromString(summary.StatusColor)!;
        StartTime.Text = run.StartedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Not started";
        var duration = run.StartedUtc.HasValue ? (run.CompletedUtc ?? DateTimeOffset.UtcNow) - run.StartedUtc.Value : TimeSpan.Zero;
        Duration.Text = duration.ToString(@"dd\.hh\:mm\:ss");
        PayloadSize.Text = summary.SizeText;
        ServerName.Text = run.Connection.Server;
        ObjectCount.Text = $"Backup objects: {run.Databases.Length}";
        BackupObjects.ItemsSource = run.Databases.Select(item => BackupObjectView.Create(run, item)).ToArray();
        RunState.Text = $"Job {run.Status} · {run.Type}";
        entries = observed is { Length: > 0 } ? observed : RunLogProjection.FromSnapshot(run);
        EvidenceNote.Text = observed is { Length: > 0 }
            ? "Observed stage transitions · local times · closing this log does not stop the backup"
            : "Older record: final stage evidence. A dash means the step timestamp was not recorded.";
        UpdateFilter();
        ExportButton.IsEnabled = true;
        if (run.Status is RunStatus.Queued or RunStatus.Running)
        {
            refreshTimer.Start();
        }
        else
        {
            refreshTimer.Stop();
        }
    }

    private void UpdateFilter()
    {
        if (LogRecords is null)
        {
            return;
        }

        IEnumerable<RunLogEntry> filtered = entries;
        if (LogFilter.SelectedIndex == 1)
        {
            filtered = entries.Where(item => item.Status is "Failed" or "Unknown" or "Interrupted");
        }
        else if (LogFilter.SelectedIndex == 2)
        {
            filtered = entries.Where(item => item.Status == "Succeeded");
        }

        LogRecords.ItemsSource = filtered.ToArray();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (currentRun is null || busy)
        {
            return;
        }

        RetryButton.IsEnabled = false;
        try
        {
            var response = await sendAsync(new ServiceRequest("RetryDestinations", RunId: currentRun.Id));
            EvidenceNote.Text = response.Message;
            await LoadAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EvidenceNote.Text = "The retry could not be confirmed. Refresh the run before trying again.";
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateFilter();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "Export job log — contains database names and file paths",
            Filter = "Text log (*.txt)|*.txt",
            FileName = "SqlBackup-log-" + (runId ?? jobId).ToString("N") + ".txt"
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var output = new StringBuilder(Details.Text);
        if (currentRun is not null)
        {
            output.AppendLine();
            foreach (var entry in entries)
            {
                output.AppendLine($"{entry.CreatedUtc:u} {entry.Stage} · {entry.Status} · {entry.Database} · {entry.Message}");
            }
        }

        try
        {
            await File.WriteAllTextAsync(picker.FileName, output.ToString(), Encoding.UTF8);
            EvidenceNote.Text = "Log exported. Keep this file private; it includes database names and paths.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            EvidenceNote.Text = "Could not export the log. Check the selected folder and permissions.";
        }
    }
}
