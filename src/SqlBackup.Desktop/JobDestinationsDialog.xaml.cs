using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class JobDestinationsDialog : Window
{
    private readonly BackupJob job;
    private readonly string sqlPassword;
    private readonly Func<ServiceRequest, Task<ServiceResponse>> sendAsync;
    private readonly ObservableCollection<BackupDestination> destinations;
    private readonly Dictionary<Guid, DestinationPassword> passwords;
    private Guid? editingId;
    private bool closed;

    public BackupJob? SelectedJob { get; private set; }
    public DestinationPassword[] SelectedPasswords { get; private set; } = [];
    public StoragePasswords SelectedSourcePasswords { get; private set; } = new();

    public JobDestinationsDialog(BackupJob job, string sqlPassword,
        Func<ServiceRequest, Task<ServiceResponse>> sendAsync, DestinationPassword[]? pendingPasswords = null,
        StoragePasswords? sourcePasswords = null, DestinationKind? newDestinationKind = null)
    {
        InitializeComponent();
        Closed += (_, _) => closed = true;
        this.job = job;
        this.sqlPassword = sqlPassword;
        this.sendAsync = sendAsync;
        destinations = new ObservableCollection<BackupDestination>(BackupDestinations.Read(job));
        passwords = (pendingPasswords ?? []).ToDictionary(item => item.DestinationId);
        DestinationList.ItemsSource = destinations;
        TemporaryFolder.Text = job.TemporaryFolder ?? BackupLocations.DefaultTemporaryFolder;
        SourceFolder.Text = job.Storage?.ServiceSourceFolder ?? string.Empty;
        SourceUser.Text = job.Storage?.SourceUserName ?? string.Empty;
        SourcePassword.Password = sourcePasswords?.Source ?? string.Empty;
        DestinationEditor.IsEnabled = false;
        if (newDestinationKind.HasValue)
        {
            BeginAddDestination(newDestinationKind.Value);
        }
        else if (destinations.Count > 0)
        {
            DestinationList.SelectedIndex = 0;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var menu = DestinationKindMenu.Create(BeginAddDestination);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    public void BeginAddDestination(DestinationKind kind)
    {
        DestinationList.SelectedItem = null;
        LoadDestination(new BackupDestination(Guid.NewGuid(), string.Empty, kind, string.Empty));
        DestinationEditor.BringIntoView();
        DestinationName.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DestinationList.SelectedItem is BackupDestination destination)
        {
            destinations.Remove(destination);
            passwords.Remove(destination.Id);
            editingId = null;
            DestinationEditor.IsEnabled = false;
        }
    }

    private void Destination_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DestinationList.SelectedItem is BackupDestination destination)
        {
            LoadDestination(destination);
        }
    }

    private void LoadDestination(BackupDestination destination)
    {
        editingId = destination.Id;
        DestinationEditor.IsEnabled = true;
        DestinationName.Text = destination.Name;
        Kind.SelectedIndex = (int)destination.Kind;
        Location.Text = destination.Location;
        Prefix.Text = destination.Prefix;
        Region.Text = destination.Region ?? string.Empty;
        UserName.Text = destination.UserName ?? string.Empty;
        RetentionDays.Text = destination.RetentionDays.ToString();
        passwords.TryGetValue(destination.Id, out var password);
        Secret.Password = password?.Secret ?? string.Empty;
        SessionToken.Password = password?.SessionToken ?? string.Empty;
        RefreshFields();
    }

    private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocationLabel is not null)
        {
            RefreshFields();
        }
    }

    private void RefreshFields()
    {
        var kind = (DestinationKind)Math.Max(0, Kind.SelectedIndex);
        CloudFields.Visibility = kind == DestinationKind.Folder ? Visibility.Collapsed : Visibility.Visible;
        RegionFields.Visibility = kind == DestinationKind.AmazonS3 ? Visibility.Visible : Visibility.Collapsed;
        SessionFields.Visibility = RegionFields.Visibility;
        UserFields.Visibility = kind == DestinationKind.AzureBlob ? Visibility.Collapsed : Visibility.Visible;
        LocationLabel.Text = kind switch
        {
            DestinationKind.AzureBlob => "Container URL (https://account.blob.core.windows.net/container, without SAS)",
            DestinationKind.AmazonS3 => "Bucket name",
            _ => "Folder path (local or UNC)"
        };
        UserLabel.Text = kind == DestinationKind.AmazonS3 ? "AWS access key ID" : "Share user name (optional)";
        SecretLabel.Text = kind switch
        {
            DestinationKind.AzureBlob => "Container SAS token (read, create/write; delete for reviewed cleanup)",
            DestinationKind.AmazonS3 => "AWS secret access key",
            _ => "Share password (optional when using the service identity)"
        };
    }

    public bool ApplyDestination()
    {
        try
        {
            if (!editingId.HasValue)
            {
                return true;
            }

            if (!int.TryParse(RetentionDays.Text, out int days))
            {
                throw new ArgumentException("Enter whole-number retention days.");
            }

            var kind = (DestinationKind)Kind.SelectedIndex;
            var destination = new BackupDestination(editingId.Value, DestinationName.Text.Trim(), kind,
                Location.Text.Trim().TrimEnd('/'), kind == DestinationKind.Folder ? string.Empty : Prefix.Text.Trim().Trim('/'),
                kind == DestinationKind.AmazonS3 ? Region.Text.Trim() : null,
                kind == DestinationKind.AzureBlob ? null : Blank(UserName.Text.Trim()), days);
            DestinationValidation.Validate(destination);
            var supplied = new DestinationPassword(destination.Id, Blank(Secret.Password), Blank(SessionToken.Password));
            passwords[destination.Id] = supplied;
            int index = destinations.ToList().FindIndex(item => item.Id == destination.Id);
            if (index < 0)
            {
                destinations.Add(destination);
            }
            else
            {
                destinations[index] = destination;
            }

            DestinationList.SelectedItem = destination;
            Status.Text = "Destination applied to the draft. Save & Close, then Save Job to persist it.";
            return true;
        }
        catch (ArgumentException exception)
        {
            Status.Text = exception.Message;
            return false;
        }
    }

    public BackupJob ReadDraft()
    {
        var draft = job with
        {
            Destinations = destinations.ToArray(),
            TemporaryFolder = BackupLocations.NormalizeTemporaryFolder(TemporaryFolder.Text),
            Storage = new StoragePolicy(ServiceSourceFolder: Blank(SourceFolder.Text.Trim()), SourceUserName: Blank(SourceUser.Text.Trim()))
        };
        DestinationValidation.ValidateAll(draft);
        return draft;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ApplyDestination();
    }

    public async Task<bool> CheckAndApplyAsync()
    {
        if (!ApplyDestination())
        {
            return false;
        }

        Editor.IsEnabled = false;
        SaveButton.IsEnabled = false;
        TestButton.IsEnabled = false;
        try
        {
            var draft = ReadDraft();
            var response = await sendAsync(new ServiceRequest("CheckDestination", Job: draft, Password: sqlPassword));
            if (closed)
            {
                return false;
            }

            if (!response.Success)
            {
                Status.Text = response.Message;
                return false;
            }

            SelectedJob = draft;
            SelectedPasswords = passwords.Values.ToArray();
            SelectedSourcePasswords = new StoragePasswords(Source: Blank(SourcePassword.Password));
            return true;
        }
        catch (ArgumentException exception)
        {
            Status.Text = exception.Message;
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status.Text = "The SQL output folder could not be checked. Check SQL connectivity and folder access.";
            return false;
        }
        finally
        {
            Editor.IsEnabled = true;
            SaveButton.IsEnabled = true;
            TestButton.IsEnabled = true;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyDestination())
        {
            return;
        }

        Editor.IsEnabled = false;
        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        try
        {
            var response = await sendAsync(new ServiceRequest("CheckDestinations", Job: ReadDraft(),
                DestinationPasswords: passwords.Values.ToArray()));
            if (!closed)
            {
                Status.Text = response.Message;
            }
        }
        catch (ArgumentException exception)
        {
            Status.Text = exception.Message;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status.Text = "Destination access could not be confirmed. Check the service connection and try again.";
        }
        finally
        {
            Editor.IsEnabled = true;
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await CheckAndApplyAsync())
        {
            DialogResult = true;
        }
    }

    private static string? Blank(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
