using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class DestinationDialog : Window
{
    private readonly BackupJob job;
    private readonly string pendingPassword;
    private readonly Func<ServiceRequest, Task<ServiceResponse>> sendAsync;
    private bool busy;
    private bool closed;

    public string? SelectedDestination { get; private set; }
    public StoragePolicy? SelectedStorage { get; private set; }
    public string? SelectedTemporaryFolder { get; private set; }
    public StoragePasswords SelectedPasswords { get; private set; } = new();

    public DestinationDialog(BackupJob job, string pendingPassword, Func<ServiceRequest, Task<ServiceResponse>> sendAsync,
        StoragePasswords? storagePasswords = null)
    {
        InitializeComponent();
        this.job = job;
        this.pendingPassword = pendingPassword;
        this.sendAsync = sendAsync;
        FolderPath.Text = job.Destination;
        PrimaryDays.Text = (job.Storage?.PrimaryRetentionDays ?? 0).ToString();
        SecondaryFolder.Text = job.Storage?.SecondaryFolder ?? string.Empty;
        SecondaryDays.Text = (job.Storage?.SecondaryRetentionDays ?? 0).ToString();
        ServiceSource.Text = job.Storage?.ServiceSourceFolder ?? string.Empty;
        TemporaryFolder.Text = job.TemporaryFolder ?? BackupLocations.DefaultTemporaryFolder;
        DestinationUser.Text = job.Storage?.DestinationUserName ?? string.Empty;
        SecondaryUser.Text = job.Storage?.SecondaryUserName ?? string.Empty;
        SourceUser.Text = job.Storage?.SourceUserName ?? string.Empty;
        DestinationPassword.Password = storagePasswords?.Destination ?? string.Empty;
        SecondaryPassword.Password = storagePasswords?.Secondary ?? string.Empty;
        SourcePassword.Password = storagePasswords?.Source ?? string.Empty;
    }

    public bool TryApplyDestination()
    {
        try
        {
            var draft = ReadDraft();
            SelectedStorage = draft.Storage;
            SelectedDestination = draft.Destination;
            SelectedTemporaryFolder = draft.TemporaryFolder;
            SelectedPasswords = ReadPasswords();
            return true;
        }
        catch (ArgumentException exception)
        {
            Status.Text = exception.Message;
            return false;
        }
    }

    private BackupJob ReadDraft()
    {
        var draft = job with
        {
            Destination = FolderPath.Text.Trim(),
            TemporaryFolder = BackupLocations.NormalizeTemporaryFolder(TemporaryFolder.Text),
            Storage = ReadStorage()
        };
        JobValidation.ValidateDestination(draft.Destination);
        JobValidation.ValidateStorage(draft.Destination, draft.Storage);
        JobValidation.ValidateTemporaryFolder(draft);
        return draft;
    }

    private StoragePasswords ReadPasswords()
    {
        return new StoragePasswords(BlankToNull(DestinationPassword.Password),
            BlankToNull(SecondaryPassword.Password), BlankToNull(SourcePassword.Password));
    }

    public StoragePolicy ReadStorage()
    {
        if (!int.TryParse(PrimaryDays.Text, out int primary) || !int.TryParse(SecondaryDays.Text, out int secondary))
        {
            throw new ArgumentException("Enter whole-number retention days; 0 keeps all files.");
        }

        return new StoragePolicy(primary, string.IsNullOrWhiteSpace(SecondaryFolder.Text) ? null : SecondaryFolder.Text.Trim(),
            secondary, string.IsNullOrWhiteSpace(ServiceSource.Text) ? null : ServiceSource.Text.Trim(),
            BlankToNull(DestinationUser.Text.Trim()), BlankToNull(SecondaryUser.Text.Trim()), BlankToNull(SourceUser.Text.Trim()));
    }

    private static string? BlankToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

    public async Task<bool> CheckAndApplyDestinationAsync()
    {
        if (busy || closed)
        {
            return false;
        }

        SetBusy(true);
        TemporaryFolderStatus.Text = "Checking this folder through SQL Server…";
        try
        {
            var draft = ReadDraft();
            ConnectionValidation.Validate(draft.Connection);
            var response = await sendAsync(new ServiceRequest("CheckDestination", Job: draft, Password: pendingPassword));
            if (closed)
            {
                return false;
            }

            if (!response.Success)
            {
                TemporaryFolderStatus.Text = response.Message;
                return false;
            }

            TemporaryFolderStatus.Text = "SQL Server sees this folder. Actual write permissions are checked when backing up.";
            return TryApplyDestination();
        }
        catch (ArgumentException exception)
        {
            if (!closed)
            {
                TemporaryFolderStatus.Text = exception.Message;
            }
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!closed)
            {
                TemporaryFolderStatus.Text = "Temporary folder could not be checked. Check SQL connectivity and try again.";
            }
            return false;
        }
        finally
        {
            if (!closed)
            {
                SetBusy(false);
            }
        }
    }

    private void TemporaryFolder_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TemporaryFolderStatus is not null)
        {
            TemporaryFolderStatus.Text = "SQL visibility will be checked before Save & Close.";
        }
    }

    public async Task TestDestinationAsync()
    {
        if (busy || closed)
        {
            return;
        }

        SetBusy(true);
        try
        {
            ConnectionValidation.Validate(job.Connection);
            var draft = ReadDraft();
            var response = await sendAsync(new ServiceRequest("CheckDestination",
                Job: draft, Password: pendingPassword));
            if (closed)
            {
                return;
            }
            TemporaryFolderStatus.Text = response.Message;
            if (!response.Success)
            {
                return;
            }
            var storageResponse = await sendAsync(new ServiceRequest("CheckStorage", Job: draft, StoragePasswords: ReadPasswords()));
            if (closed)
            {
                return;
            }
            Status.Text = response.Message + "\n" + storageResponse.Message;
        }
        catch (ArgumentException exception)
        {
            if (!closed)
            {
                Status.Text = exception.Message;
                TemporaryFolderStatus.Text = exception.Message;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!closed)
            {
                Status.Text = "Could not test this folder. Check the service connection and this job's SQL settings.";
            }
        }
        finally
        {
            if (!closed)
            {
                SetBusy(false);
            }
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Select a folder on this computer",
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
        {
            FolderPath.Text = picker.FolderName;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        await TestDestinationAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await CheckAndApplyDestinationAsync())
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
        CancelButton.IsEnabled = true;
        if (value)
        {
            Status.Text = "Checking SQL temporary folder and service access to final folders…";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
    }
}
