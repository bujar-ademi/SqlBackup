using System.ComponentModel;
using System.Windows;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class ConnectionDialog : Window
{
    private readonly BackupJob job;
    private readonly Func<ServiceRequest, Task<ServiceResponse>> sendAsync;
    private ConnectionConfiguration? testedConnection;
    private string? testedPassword;
    private ServerInspection? inspection;
    private bool busy;

    public ConnectionDialogResult? Result { get; private set; }

    public ConnectionDialog(BackupJob job, string pendingPassword, Func<ServiceRequest, Task<ServiceResponse>> sendAsync)
    {
        InitializeComponent();
        this.job = job;
        this.sendAsync = sendAsync;
        ServerName.Text = job.Connection?.Server ?? string.Empty;
        WindowsAuthentication.IsChecked = job.Connection?.UseWindowsAuthentication ?? true;
        SqlAuthentication.IsChecked = WindowsAuthentication.IsChecked != true;
        UserName.Text = job.Connection?.UserName ?? string.Empty;
        Password.Password = pendingPassword;
        TrustCertificate.IsChecked = job.Connection?.TrustServerCertificate ?? false;
    }

    public ConnectionConfiguration ReadConfiguration()
    {
        bool windows = WindowsAuthentication.IsChecked == true;
        string? userName = windows ? null : UserName.Text.Trim();
        string server = ServerName.Text.Trim();
        bool hasPassword = job.Connection?.HasPassword == true && job.Connection.Server == server
            && job.Connection.UserName == userName && job.Connection.UseWindowsAuthentication == windows;
        return new ConnectionConfiguration(job.Connection?.Name ?? "SQL Server", server, windows, userName,
            hasPassword, TrustCertificate.IsChecked == true);
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var configuration = ReadConfiguration();
            ConnectionValidation.Validate(configuration);
            var response = await sendAsync(new ServiceRequest("Inspect",
                Job: job with { Connection = configuration }, Password: Password.Password));
            if (!response.Success || response.Inspection is null)
            {
                Status.Text = response.Message;
                return false;
            }

            inspection = response.Inspection;
            testedConnection = configuration;
            testedPassword = Password.Password;
            Status.Text = $"Connected · SQL Server {inspection.Version}\n{inspection.Edition}";
            return true;
        }
        catch (ArgumentException exception)
        {
            Status.Text = exception.Message;
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status.Text = "Could not contact AbaSqlBackup. Check that the service is running and that this app is elevated.";
            return false;
        }
        finally
        {
            FormScrollViewer.ScrollToBottom();
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await TestConnectionAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy)
        {
            return;
        }

        SetBusy(true);
        bool accepted = false;
        try
        {
            bool currentTest = testedConnection == ReadConfiguration() && testedPassword == Password.Password && inspection is not null;
            if (currentTest || await TestConnectionAsync())
            {
                Result = new ConnectionDialogResult(ReadConfiguration(), Password.Password, inspection!);
                accepted = true;
            }
        }
        finally
        {
            SetBusy(false);
        }

        if (accepted)
        {
            DialogResult = true;
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Editor.IsEnabled = !value;
        TestButton.IsEnabled = !value;
        AcceptButton.IsEnabled = !value;
        CancelButton.IsEnabled = !value;
        if (value)
        {
            Status.Text = "Testing connection…";
        }
    }

    private void Authentication_Changed(object sender, RoutedEventArgs e)
    {
        if (UserName is null || Password is null)
        {
            return;
        }

        bool sql = SqlAuthentication.IsChecked == true;
        UserName.IsEnabled = sql;
        Password.IsEnabled = sql;
        Password.Clear();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = busy;
        if (!busy)
        {
            Password.Clear();
            testedPassword = null;
        }
    }
}
