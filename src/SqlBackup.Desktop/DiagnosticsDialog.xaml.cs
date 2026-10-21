using System.ComponentModel;
using System.Windows;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class DiagnosticsDialog : Window
{
    private readonly Guid jobId;
    private readonly Func<ServiceRequest, Task<ServiceResponse>> sendAsync;
    private bool busy;

    public DiagnosticsDialog(Guid jobId, Func<ServiceRequest, Task<ServiceResponse>> sendAsync)
    {
        InitializeComponent();
        this.jobId = jobId;
        this.sendAsync = sendAsync;
    }

    public async Task CheckAsync()
    {
        if (busy)
        {
            return;
        }

        busy = true;
        RunButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        Status.Text = "Checking the saved job through the service…";
        try
        {
            var response = await sendAsync(new ServiceRequest("JobDiagnostics", JobId: jobId));
            Checks.ItemsSource = response.Diagnostics ?? [];
            Status.Text = response.Message + " These checks do not establish a successful restore.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status.Text = "Diagnostics could not be confirmed. Reconnect to the service and retry.";
        }
        finally
        {
            busy = false;
            RunButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        await CheckAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = busy;
    }
}
