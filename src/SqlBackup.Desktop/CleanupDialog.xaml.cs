using System.ComponentModel;
using System.Windows;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class CleanupDialog : Window
{
    private readonly BackupJob job;
    private readonly Func<ServiceRequest, Task<ServiceResponse>> sendAsync;
    private CleanupPreview? preview;
    private bool busy;

    public CleanupDialog(BackupJob job, Func<ServiceRequest, Task<ServiceResponse>> sendAsync)
    {
        InitializeComponent();
        this.job = job;
        this.sendAsync = sendAsync;
    }

    public async Task RefreshAsync()
    {
        await PerformAsync(async () =>
        {
            var response = await sendAsync(new ServiceRequest("CleanupPreview", JobId: job.Id));
            preview = response.Cleanup ?? throw new ArgumentException("No cleanup evidence was returned.");
            Decisions.ItemsSource = preview.Decisions;
            Status.Text = preview.Message + $" Eligible files: {preview.Decisions.Count(item => item.Delete)}.";
            await LoadAuditAsync();
        });
    }

    private async Task LoadAuditAsync()
    {
        var response = await sendAsync(new ServiceRequest("CleanupHistory", JobId: job.Id));
        Audit.Text = string.Join("\n", (response.CleanupHistory ?? []).Select(item =>
            $"{item.CreatedUtc:u} · {item.Outcome} · {item.Path}\n{item.Reason}"));
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        RefreshButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (ArgumentException exception)
        {
            preview = null;
            Status.Text = exception.Message;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            preview = null;
            Status.Text = "Cleanup could not be confirmed. Reconnect and inspect the audit before another request.";
        }
        finally
        {
            busy = false;
            RefreshButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            DeleteButton.IsEnabled = preview?.Decisions.Any(item => item.Delete) == true;
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        Status.Text = "Inspecting recorded artifacts, integrity and SQL backup history…";
        await RefreshAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (preview is null || MessageBox.Show(this,
            $"Permanently delete {preview.Decisions.Count(item => item.Delete)} eligible files shown in this preview?",
            "Confirm cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        string token = preview.Token;
        preview = null;
        await PerformAsync(async () =>
        {
            var response = await sendAsync(new ServiceRequest("CleanupExecute", JobId: job.Id, PreviewToken: token));
            Status.Text = response.Message + " Refresh to inspect the remaining files.";
            await LoadAuditAsync();
        });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = busy;
    }
}
